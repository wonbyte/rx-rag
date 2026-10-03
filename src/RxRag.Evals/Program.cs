using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RxRag.Core;
using RxRag.Core.Answering;
using RxRag.Core.Search;
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
var retriever = host.Services.GetRequiredService<IRetriever>();
var judge = host.Services.GetRequiredService<Judge>();

// The fixed refusal sentence, without its final period, so a model that
// drops the period still counts as refusing.
var refusal = PromptBuilder.NoAnswer.TrimEnd('.');

var rows = new List<(EvalCase Case, bool Pass, bool CiteHit, int DrugRank, int SectionRank, JudgeScore? Score, string Note)>();

foreach (var c in cases)
{
    // One failed call (a rate limit, a timeout, a judge reply that is not
    // valid JSON) must not stop the run. It counts as a failed case, so the
    // gate still fails, but the report shows every other result too.
    try
    {
        if (c.ExpectNoAnswer)
        {
            var reply = await answers.AskAsync(c.Question);
            var didRefuse = reply.Text.Contains(refusal, StringComparison.OrdinalIgnoreCase);
            rows.Add((c, didRefuse, false, 0, 0, null, didRefuse ? "refused" : $"answered: {Short(reply.Text)}"));
            continue;
        }

        // Retrieval check: search only, no LLM. It tells a SEARCH problem
        // (the right text never reached the model) apart from a MODEL
        // problem (the text was there, the answer was still wrong).
        // Rank = position 1..5 of the first match, 0 = not in the top 5.
        var hits = await retriever.RetrieveAsync(c.Question);
        var drugRank = RankOf(hits, h => IsDrug(h, c));
        var sectionRank = RankOf(hits, h => IsDrug(h, c) && IsSection(h, c));

        var answer = await answers.AskAsync(c.Question);
        var refused = answer.Text.Contains(refusal, StringComparison.OrdinalIgnoreCase);

        // Deterministic check: did the answer cite a source for the right drug?
        var citeHit = c.ExpectGeneric is not null
            && answer.Citations.Any(x => x.GenericName.Contains(c.ExpectGeneric, StringComparison.OrdinalIgnoreCase));

        // Model-graded check: is it grounded and on topic?
        var score = refused ? null : await judge.ScoreAsync(c.Question, answer, CancellationToken.None);
        var pass = !refused && citeHit && score is { Grounded: >= 4, Relevant: >= 4 };

        rows.Add((c, pass, citeHit, drugRank, sectionRank, score, refused ? "refused (should answer)" : score!.Reason));
    }
    catch (Exception ex)
    {
        rows.Add((c, false, false, 0, 0, null, $"error: {ex.GetType().Name}: {ex.Message}"));
    }
}

// ---- Metrics and gate ----
var refusalRows = rows.Where(r => r.Case.ExpectNoAnswer).ToList();
var answerRows = rows.Where(r => !r.Case.ExpectNoAnswer).ToList();
var scored = answerRows.Where(r => r.Score is not null).ToList();

var refusalOk = refusalRows.All(r => r.Pass);
var passRate = Rate(answerRows, r => r.Pass);
var citeRate = Rate(answerRows, r => r.CiteHit);
var meanGrounded = scored.Count == 0 ? 0.0 : scored.Average(r => r.Score!.Grounded);

// Retrieval metrics. MRR (mean reciprocal rank): 1.0 if the right section
// is always first, 0.5 if it is usually second, 0 if never found.
var drugHit = Rate(answerRows, r => r.DrugRank > 0);
var sectionHit = Rate(answerRows, r => r.SectionRank > 0);
var sectionMrr = answerRows.Count == 0 ? 0.0 : answerRows.Average(r => r.SectionRank > 0 ? 1.0 / r.SectionRank : 0.0);

// Safety cases (refusals) must ALL pass. Quality cases have a threshold,
// because a few flaky misses should not block every deploy.
// Retrieval metrics are reported, not gated, until a few runs show their
// normal range. Then a floor (for example section hit >= 0.8) can join.
var gate = refusalOk && passRate >= 0.8 && meanGrounded >= 4.0;

var report = new StringBuilder();
report.AppendLine("| Case | Pass | Cite | Drug@ | Section@ | Grounded | Relevant | Note |");
report.AppendLine("|---|---|---|---|---|---|---|---|");
foreach (var r in rows)
{
    var drugCol = r.Case.ExpectNoAnswer ? "-" : Rank(r.DrugRank);
    var sectionCol = r.Case.ExpectNoAnswer ? "-" : Rank(r.SectionRank);
    report.AppendLine(CultureInfo.InvariantCulture,
        $"| {r.Case.Id} | {(r.Pass ? "yes" : "NO")} | {(r.CiteHit ? "yes" : "-")} | {drugCol} | {sectionCol} | {r.Score?.Grounded.ToString(CultureInfo.InvariantCulture) ?? "-"} | {r.Score?.Relevant.ToString(CultureInfo.InvariantCulture) ?? "-"} | {Short(r.Note)} |");
}

report.AppendLine();
report.AppendLine(CultureInfo.InvariantCulture,
    $"Answers: refusals all correct: {refusalOk}. Pass rate: {passRate:P0}. Cite hit rate: {citeRate:P0}. Mean grounded: {meanGrounded:F2}.");
report.AppendLine(CultureInfo.InvariantCulture,
    $"Retrieval (top 5, not gated): drug hit: {drugHit:P0}. Section hit: {sectionHit:P0}. Section MRR: {sectionMrr:F2}.");
report.AppendLine(gate ? "GATE: PASS" : "GATE: FAIL");

Console.WriteLine(report);

// In GitHub Actions, this file shows as a nice table on the run page.
if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
{
    await File.AppendAllTextAsync(summary, report.ToString());
}

return gate ? 0 : 1;

static bool IsDrug(RetrievedChunk hit, EvalCase c) =>
    c.ExpectGeneric is not null && hit.GenericName.Contains(c.ExpectGeneric, StringComparison.OrdinalIgnoreCase);

static bool IsSection(RetrievedChunk hit, EvalCase c) =>
    c.ExpectSections is not null && c.ExpectSections.Contains(hit.SectionTitle, StringComparer.OrdinalIgnoreCase);

// 1-based position of the first match, or 0 if none.
static int RankOf(IReadOnlyList<RetrievedChunk> hits, Func<RetrievedChunk, bool> match)
{
    for (var i = 0; i < hits.Count; i++)
    {
        if (match(hits[i]))
        {
            return i + 1;
        }
    }

    return 0;
}

static double Rate<T>(List<T> items, Func<T, bool> test) =>
    items.Count == 0 ? 1.0 : items.Count(test) / (double)items.Count;

static string Rank(int rank) => rank == 0 ? "miss" : rank.ToString(CultureInfo.InvariantCulture);

static string Short(string text) =>
    (text.Length > 80 ? text[..80] + "..." : text).Replace("|", "/", StringComparison.Ordinal).ReplaceLineEndings(" ");