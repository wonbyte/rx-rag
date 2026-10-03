using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RxRag.Core.Answering;
using RxRag.Core.Search;
using Xunit;

namespace RxRag.Tests;

// Fakes for the retriever and the model. We test OUR logic (no sources,
// citation mapping, streaming order) without calling Azure.
public class AnswerServiceTests
{
    private static readonly RetrievedChunk Warn =
        new("id1", "Advil", "ibuprofen", "Warnings", "May cause stomach bleeding.", 1.0);

    private static readonly RetrievedChunk Inter =
        new("id2", "Advil", "ibuprofen", "Drug interactions", "Ask a doctor if you take warfarin.", 0.9);

    private sealed class FakeRetriever(params RetrievedChunk[] chunks) : IRetriever
    {
        public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string question, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RetrievedChunk>>(chunks);
    }

    private sealed class FakeChat(params string[] pieces) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(pieces))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            foreach (var piece in pieces)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static AnswerService Service(IRetriever r, IChatClient c) =>
        new(r, c, NullLogger<AnswerService>.Instance);

    [Fact]
    public async Task NoSources_NoAnswer_ModelNotCalled()
    {
        var chat = new FakeChat("should not be used");

        var answer = await Service(new FakeRetriever(), chat).AskAsync("Anything?");

        Assert.Equal(PromptBuilder.NoAnswer, answer.Text);
        Assert.Empty(answer.Citations);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task Ask_MapsOnlyCitedSources()
    {
        var answer = await Service(new FakeRetriever(Warn, Inter), new FakeChat("It may cause stomach bleeding [1]."))
            .AskAsync("Risks?");

        var citation = Assert.Single(answer.Citations);
        Assert.Equal(1, citation.Number);
        Assert.Equal("id1", citation.ChunkId);
        Assert.Equal("Advil (ibuprofen), Warnings", citation.Source);
        Assert.Equal("May cause stomach bleeding.", citation.Excerpt);
    }

    [Fact]
    public async Task Stream_DeltasThenDoneWithCitations()
    {
        // "[2]" is split across pieces on purpose: citations must come
        // from the FULL text, not from single pieces.
        var service = Service(new FakeRetriever(Warn, Inter), new FakeChat("Ask a doctor ", "[", "2]."));

        var events = new List<AnswerEvent>();
        await foreach (var e in service.StreamAsync("Warfarin?"))
        {
            events.Add(e);
        }

        Assert.Equal(["delta", "delta", "delta", "done"], events.Select(e => e.Type));
        Assert.Equal("Ask a doctor [2].", string.Concat(events.Take(3).Select(e => e.Text)));
        Assert.Equal("id2", Assert.Single(events[^1].Citations!).ChunkId);
    }

    [Fact]
    public async Task Stream_NoSources_NoAnswerThenDone()
    {
        var chat = new FakeChat("unused");
        var events = new List<AnswerEvent>();
        await foreach (var e in Service(new FakeRetriever(), chat).StreamAsync("Anything?"))
        {
            events.Add(e);
        }

        Assert.Equal(PromptBuilder.NoAnswer, events[0].Text);
        Assert.Equal("done", events[1].Type);
        Assert.Equal(0, chat.Calls);
    }
}