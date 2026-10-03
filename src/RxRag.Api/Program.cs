using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using RxRag.Core;
using RxRag.Core.Answering;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRxRag(builder.Configuration);

// RFC 7807 "problem details" JSON for all errors. No stack traces leak out.
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

// Every question costs tokens. A rate limit caps cost and abuse.
// Per-instance here; use a per-user key behind auth in production.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddFixedWindowLimiter("ask", w =>
    {
        w.PermitLimit = 20;
        w.Window = TimeSpan.FromMinutes(1);
        w.QueueLimit = 0;
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

// In Docker, the React build sits in wwwroot. The API serves it, so the
// page and the API share one origin: no CORS setup needed.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/healthz");

var api = app.MapGroup("/api").RequireRateLimiting("ask");

api.MapPost("/ask", async Task<Results<Ok<Answer>, ValidationProblem>> (
    AskRequest request, AnswerService answers, CancellationToken ct) =>
{
    if (!TryGetQuestion(request, out var question, out var errors))
    {
        return TypedResults.ValidationProblem(errors);
    }

    return TypedResults.Ok(await answers.AskAsync(question, ct));
});

// Streaming: the user sees words appear right away instead of waiting
// for the full answer. Same total time, much better FEEL.
api.MapPost("/ask/stream", IResult (AskRequest request, AnswerService answers, CancellationToken ct) =>
{
    if (!TryGetQuestion(request, out var question, out var errors))
    {
        return TypedResults.ValidationProblem(errors);
    }

    return TypedResults.ServerSentEvents(Stream(answers, question, ct));
});

// Any other GET path loads the React app.
app.MapFallbackToFile("index.html");

app.Run();

// Input rules: required, max 500 characters. Long inputs cost tokens and
// are a common way to smuggle prompt injection text.
static bool TryGetQuestion(AskRequest request, out string question, out Dictionary<string, string[]> errors)
{
    question = request.Question?.Trim() ?? "";
    errors = [];

    if (question.Length == 0)
    {
        errors["question"] = ["Question is required."];
    }
    else if (question.Length > 500)
    {
        errors["question"] = ["Question must be 500 characters or less."];
    }

    return errors.Count == 0;
}

// Each AnswerEvent becomes one SSE event. The SSE "event:" name is the
// event type ("delta" or "done"); "data:" is the JSON body.
// The CancellationToken fires when the browser disconnects, which stops
// the model call too, so we do not pay for tokens nobody reads.
static async IAsyncEnumerable<SseItem<AnswerEvent>> Stream(
    AnswerService answers, string question, [EnumeratorCancellation] CancellationToken ct)
{
    await foreach (var evt in answers.StreamAsync(question, ct))
    {
        yield return new SseItem<AnswerEvent>(evt, evt.Type);
    }
}

internal sealed record AskRequest(string? Question);