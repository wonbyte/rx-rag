using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RxRag.Core;
using RxRag.Core.Answering;
using RxRag.Evals;

// Usage: dotnet run --project src/RxRag.Evals [path/to/golden.json]
// Exit code 0 = gate passed. Exit code 1 = gate failed (CI stops the deploy).

var builder = Host.CreateApplicationBuilder();

// HttpClient and Polly log every request at Information. Too noisy here.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Polly", LogLevel.Warning);

builder.Services.AddRxRag(builder.Configuration);
builder.Services.AddSingleton<Judge>();
using var host = builder.Build();

var path = args.Length > 0 ? args[0] : "evals/golden.json";
var cases = JsonSerializer.Deserialize<List<EvalCase>>(await File.ReadAllTextAsync(path), JsonSerializerOptions.Web) ?? [];

var answers = host.Services.GetRequiredService<AnswerService>();
var judge = host.Services.GetRequiredService<Judge>();

// The fixed refusal sentence, without its final period, so a model that
// drops the period still counts as refusing.
var refusal = PromptBuilder.NoAnswer.TrimEnd('.');

var rows = new List<(EvalCase Case, bool Pass, bool CiteHit, JudgeScore? Score, string Note)>();

foreach (var c in cases)
{
    // One failed call (a rate limit, a timeout, a judge reply that is not
    // valid JSON) must not stop the run. It counts as a failed case, so the
    // gate still fails, but the report shows every other result too.
    try
    {
        var answer = await answers.AskAsync(c.Question);
        var refused = answer.Text.Contains(refusal, StringComparison.OrdinalIgnoreCase);

        if (c.ExpectNoAnswer)
        {
            rows.Add((c, refused, false, null, refused ? "refused" : $"answered: {Short(answer.Text)}"));
            continue;
        }

        // Deterministic check: did it cite a label for the right drug?
        var citeHit = c.ExpectGeneric is not null
            && answer.Citations.Any(x => x.GenericName.Contains(c.ExpectGeneric, StringComparison.OrdinalIgnoreCase));

        // Model-graded check: is it grounded and on topic?
        var score = refused ? null : await judge.ScoreAsync(c.Question, answer, CancellationToken.None);
        var pass = !refused && citeHit && score is { Grounded: >= 4, Relevant: >= 4 };

        rows.Add((c, pass, citeHit, score, refused ? "refused (should answer)" : score!.Reason));
    }
    catch (Exception ex)
    {
        rows.Add((c, false, false, null, $"error: {ex.GetType().Name}: {ex.Message}"));
    }
}

// ---- Metrics and gate ----
var refusalRows = rows.Where(r => r.Case.ExpectNoAnswer).ToList();
var answerRows = rows.Where(r => !r.Case.ExpectNoAnswer).ToList();
var scored = answerRows.Where(r => r.Score is not null).ToList();

var refusalOk = refusalRows.All(r => r.Pass);
var passRate = answerRows.Count == 0 ? 1.0 : answerRows.Count(r => r.Pass) / (double)answerRows.Count;
var citeRate = answerRows.Count == 0 ? 1.0 : answerRows.Count(r => r.CiteHit) / (double)answerRows.Count;
var meanGrounded = scored.Count == 0 ? 0.0 : scored.Average(r => r.Score!.Grounded);

// Safety cases (refusals) must ALL pass. Quality cases have a threshold,
// because a few flaky misses should not block every deploy.
var gate = refusalOk && passRate >= 0.8 && meanGrounded >= 4.0;

var report = new StringBuilder();
report.AppendLine("| Case | Pass | Cite | Grounded | Relevant | Note |");
report.AppendLine("|---|---|---|---|---|---|");
foreach (var r in rows)
{
    report.AppendLine(CultureInfo.InvariantCulture,
        $"| {r.Case.Id} | {(r.Pass ? "yes" : "NO")} | {(r.CiteHit ? "yes" : "-")} | {r.Score?.Grounded.ToString(CultureInfo.InvariantCulture) ?? "-"} | {r.Score?.Relevant.ToString(CultureInfo.InvariantCulture) ?? "-"} | {Short(r.Note)} |");
}

report.AppendLine();
report.AppendLine(CultureInfo.InvariantCulture,
    $"Refusals all correct: {refusalOk}. Pass rate: {passRate:P0}. Cite hit rate: {citeRate:P0}. Mean grounded: {meanGrounded:F2}.");
report.AppendLine(gate ? "GATE: PASS" : "GATE: FAIL");

Console.WriteLine(report);

// In GitHub Actions, this file shows as a nice table on the run page.
if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
{
    await File.AppendAllTextAsync(summary, report.ToString());
}

return gate ? 0 : 1;

static string Short(string text) =>
    (text.Length > 80 ? text[..80] + "..." : text).Replace("|", "/", StringComparison.Ordinal).ReplaceLineEndings(" ");