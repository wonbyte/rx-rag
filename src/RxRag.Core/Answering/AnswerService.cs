using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RxRag.Core.Search;

namespace RxRag.Core.Answering;

/// <summary>One source the answer used.</summary>
/// <param name="Number">The [n] number in the answer text.</param>
/// <param name="Source">Source line, for example "Advil (ibuprofen), Warnings".</param>
/// <param name="GenericName">Generic name. Evals check this.</param>
/// <param name="ChunkId">Chunk ID, for tracing.</param>
/// <param name="Excerpt">The chunk text, so the user can check the claim.</param>
public sealed record Citation(int Number, string Source, string GenericName, string ChunkId, string Excerpt);

/// <summary>A full answer.</summary>
/// <param name="Text">Answer text with [n] markers.</param>
/// <param name="Citations">Only the sources the text actually cites.</param>
public sealed record Answer(string Text, IReadOnlyList<Citation> Citations);

/// <summary>One streaming event: a piece of text, or the final citations.</summary>
/// <param name="Type"><see cref="Delta"/> or <see cref="Done"/>.</param>
/// <param name="Text">Text piece, for delta events.</param>
/// <param name="Citations">Citations, for the done event.</param>
public sealed record AnswerEvent(string Type, string? Text = null, IReadOnlyList<Citation>? Citations = null)
{
    /// <summary>A piece of answer text.</summary>
    public const string Delta = "delta";

    /// <summary>The end, with citations.</summary>
    public const string Done = "done";
}

/// <summary>The "answer a question" pipeline: retrieve, prompt, generate, cite.</summary>
/// <param name="retriever">Finds chunks.</param>
/// <param name="chat">The chat model.</param>
/// <param name="logger">Logger.</param>
public sealed partial class AnswerService(IRetriever retriever, IChatClient chat, ILogger<AnswerService> logger)
{
    /// <summary>Answers one question in one response.</summary>
    /// <param name="question">User question.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The answer with citations.</returns>
    public async Task<Answer> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var sources = await GatherSourcesAsync(question, cancellationToken);

        // No sources: do not call the model at all. It saves cost, and
        // the model has nothing to ground on, so any answer would be a guess.
        if (sources.Count == 0)
        {
            return new Answer(PromptBuilder.NoAnswer, []);
        }

        // No Temperature or MaxOutputTokens here on purpose: some newer
        // (reasoning) models reject a custom temperature. Set limits per
        // model in production, and cap cost with the API rate limiter.
        var response = await chat.GetResponseAsync(
            PromptBuilder.Build(question, sources), cancellationToken: cancellationToken);

        var text = response.Text.Trim();
        return new Answer(text, Cite(text, sources));
    }

    /// <summary>Answers one question as a stream of text pieces, then citations.</summary>
    /// <param name="question">User question.</param>
    /// <param name="cancellationToken">Stops the stream (for example the user closed the page).</param>
    /// <returns>Delta events, then one done event.</returns>
    public async IAsyncEnumerable<AnswerEvent> StreamAsync(
        string question, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var sources = await GatherSourcesAsync(question, cancellationToken);

        if (sources.Count == 0)
        {
            yield return new AnswerEvent(AnswerEvent.Delta, Text: PromptBuilder.NoAnswer);
            yield return new AnswerEvent(AnswerEvent.Done, Citations: []);
            yield break;
        }

        // Keep the full text: citations can only be found after the end,
        // because a "[2]" may arrive split across two pieces.
        var full = new StringBuilder();
        await foreach (var update in chat.GetStreamingResponseAsync(
            PromptBuilder.Build(question, sources), cancellationToken: cancellationToken))
        {
            var piece = update.Text;
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            full.Append(piece);
            yield return new AnswerEvent(AnswerEvent.Delta, Text: piece);
        }

        yield return new AnswerEvent(AnswerEvent.Done, Citations: Cite(full.ToString(), sources));
    }

    // Label chunks from search, then a drug-class fact for each drug the
    // question names. Class facts go LAST, so label text keeps the low
    // numbers. If search found nothing, we add nothing: a class fact alone
    // is not enough to answer a question about a drug's label.
    private async Task<IReadOnlyList<RetrievedChunk>> GatherSourcesAsync(string question, CancellationToken ct)
    {
        var found = await retriever.RetrieveAsync(question, ct);
        if (found.Count == 0)
        {
            LogSources(logger, 0, 0);
            return found;
        }

        var classes = DrugClasses.FindIn(question).Select(DrugClasses.ToSource).ToList();
        LogSources(logger, found.Count, classes.Count);
        return [.. found, .. classes];
    }

    // Maps each [n] in the answer back to the source it points at.
    // CitedNumbers already drops numbers outside 1..sources.Count, so
    // sources[n - 1] is always a valid index here.
    private static List<Citation> Cite(string text, IReadOnlyList<RetrievedChunk> sources) =>
        PromptBuilder.CitedNumbers(text, sources.Count)
            .Select(n =>
            {
                var s = sources[n - 1];
                return new Citation(n, s.Citation, s.GenericName, s.Id, s.Content);
            })
            .ToList();

    [LoggerMessage(Level = LogLevel.Information, Message = "Retrieved {Chunks} chunks and {Classes} drug class facts")]
    private static partial void LogSources(ILogger logger, int chunks, int classes);
}