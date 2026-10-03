using RxRag.Core.Chunks;
using RxRag.Core.Labels;
using Xunit;

namespace RxRag.Tests;

public class ChunkerTests
{
    private const string SetId = "8ec3d5c4-1b2a-4c3d-9e8f-0a1b2c3d4e5f";

    private static DrugLabel Label(params LabelSection[] sections) =>
        new(SetId, "Advil", "ibuprofen", default, sections);

    private static DrugLabel Label(string text) =>
        Label(new LabelSection("warnings", "Warnings", text));

    // 100 sentences, each about 27 characters.
    private static string LongText() =>
        string.Join(" ", Enumerable.Range(0, 100).Select(i => $"Sentence number {i} is here."));

    private static readonly Chunker Small = new(new ChunkerOptions(MaxChars: 200, OverlapChars: 50));

    [Fact]
    public void ShortText_OneChunk_WhitespaceNormalized()
    {
        var chunks = Small.Split(Label("  Do not   use.\n\nAsk a doctor. ")).ToList();

        var chunk = Assert.Single(chunks);
        Assert.Equal("Do not use. Ask a doctor.", chunk.Text);
    }

    [Fact]
    public void LongText_EveryChunkWithinMax()
    {
        var chunks = Small.Split(Label(LongText())).ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 200, $"Too long: {c.Text.Length}"));
    }

    [Fact]
    public void LongText_OrdinalsAreSequential()
    {
        var chunks = Small.Split(Label(LongText())).ToList();

        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void LongText_NextChunkStartsWithLastSentenceOfPrevious()
    {
        var chunks = Small.Split(Label(LongText())).ToList();

        for (var i = 0; i < chunks.Count - 1; i++)
        {
            var prev = chunks[i].Text;
            var lastSentence = prev[(prev.LastIndexOf(". ", StringComparison.Ordinal) + 2)..];
            Assert.StartsWith(lastSentence, chunks[i + 1].Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LongText_NoSentenceIsLost()
    {
        var chunks = Small.Split(Label(LongText())).ToList();

        for (var i = 0; i < 100; i++)
        {
            var sentence = $"Sentence number {i} is here.";
            Assert.Contains(chunks, c => c.Text.Contains(sentence, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void GiantWord_IsHardSplit_AndNothingIsLost()
    {
        var word = new string('x', 500);

        var chunks = Small.Split(Label(word)).ToList();

        Assert.All(chunks, c => Assert.True(c.Text.Length <= 200));
        Assert.Equal(word, string.Concat(chunks.Select(c => c.Text)));
    }

    [Fact]
    public void EachSection_StartsAtOrdinalZero()
    {
        var label = Label(
            new LabelSection("warnings", "Warnings", LongText()),
            new LabelSection("drug_interactions", "Drug interactions", "Ask a doctor."));

        var interactions = Small.Split(label).Where(c => c.SectionKey == "drug_interactions").ToList();

        Assert.Equal(0, Assert.Single(interactions).Ordinal);
    }

    [Fact]
    public void Split_IsDeterministic()
    {
        var a = Small.Split(Label(LongText())).Select(c => (c.Id, c.Text)).ToList();
        var b = Small.Split(Label(LongText())).Select(c => (c.Id, c.Text)).ToList();

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(50, 0)]    // max too small
    [InlineData(200, 100)] // overlap is half of max
    [InlineData(200, -1)]  // negative overlap
    public void BadOptions_Throw(int max, int overlap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Chunker(new ChunkerOptions(max, overlap)));
    }
}