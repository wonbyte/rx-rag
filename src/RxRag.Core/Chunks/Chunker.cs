using System.Text.RegularExpressions;
using RxRag.Core.Labels;

namespace RxRag.Core.Chunks;

/// <summary>Settings for <see cref="Chunker"/>.</summary>
/// <param name="MaxChars">
/// Largest chunk, in characters. About 4 characters is 1 token, so 1200
/// characters is about 300 tokens. Small chunks match questions more
/// precisely. Big chunks keep more context. 1200 is a common middle value.
/// </param>
/// <param name="OverlapChars">
/// How much text from the end of one chunk repeats at the start of the next.
/// Overlap stops a fact from being cut in half at a chunk border.
/// </param>
public sealed record ChunkerOptions(int MaxChars = 1200, int OverlapChars = 200);

/// <summary>
/// Cuts each label section into chunks. Cuts happen at sentence ends,
/// so a chunk never stops in the middle of a sentence unless one sentence
/// is longer than the max size.
/// </summary>
/// <remarks>
/// Why chunk per SECTION and not per label: a chunk must never mix
/// "Uses" text with "Warnings" text. If it did, the citation would name
/// only one section and point the user to the wrong place.
/// </remarks>
public sealed partial class Chunker
{
    private readonly int _max;
    private readonly int _overlap;

    /// <summary>Creates a chunker and checks its settings.</summary>
    /// <param name="options">Chunk size settings.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// MaxChars is under 100, OverlapChars is negative, or OverlapChars is half of MaxChars or more.
    /// </exception>
    public Chunker(ChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxChars, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(options.OverlapChars);

        // Overlap of half or more means every chunk is mostly a copy of the
        // last one. That wastes embedding cost and fills the prompt with repeats.
        if (options.OverlapChars >= options.MaxChars / 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "OverlapChars must be less than half of MaxChars.");
        }

        _max = options.MaxChars;
        _overlap = options.OverlapChars;
    }

    /// <summary>Splits every section of a label into chunks.</summary>
    /// <param name="label">The label to split.</param>
    /// <returns>Chunks in label order. Ordinals restart at 0 for each section.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="label"/> is null.</exception>
    public IEnumerable<Chunk> Split(DrugLabel label)
    {
        // Check the argument NOW, then return the lazy iterator.
        // If this method itself used "yield", the null check would only run
        // when someone starts looping, far away from the real bug.
        ArgumentNullException.ThrowIfNull(label);
        return SplitIterator(label);
    }

    private IEnumerable<Chunk> SplitIterator(DrugLabel label)
    {
        foreach (var section in label.Sections)
        {
            var ordinal = 0;
            foreach (var piece in Pack(Normalize(section.Text)))
            {
                yield return Chunk.Create(label, section, ordinal++, piece);
            }
        }
    }

    // Collapse every run of spaces, tabs, and newlines to one space.
    // openFDA text has random line breaks from the PDF source.
    private static string Normalize(string text) => Whitespace().Replace(text, " ").Trim();

    // Greedy packing: add sentences to a window until the next one does
    // not fit. Then emit the window and start a new one with a small tail
    // (the overlap) from the old one.
    private IEnumerable<string> Pack(string text)
    {
        if (text.Length <= _max)
        {
            yield return text;
            yield break;
        }

        var window = new List<string>();
        var length = 0;

        foreach (var unit in Units(text))
        {
            var needed = window.Count == 0 ? unit.Length : length + 1 + unit.Length;
            if (needed > _max && window.Count > 0)
            {
                yield return string.Join(' ', window);

                window = Tail(window);
                length = JoinedLength(window);

                // If overlap + this unit is still too big, drop the overlap.
                // Size limit wins over overlap.
                if (window.Count > 0 && length + 1 + unit.Length > _max)
                {
                    window.Clear();
                    length = 0;
                }
            }

            length = window.Count == 0 ? unit.Length : length + 1 + unit.Length;
            window.Add(unit);
        }

        if (window.Count > 0)
        {
            yield return string.Join(' ', window);
        }
    }

    // The last few sentences of a window that fit in the overlap budget.
    private List<string> Tail(List<string> window)
    {
        var tail = new List<string>();
        var length = 0;

        for (var i = window.Count - 1; i >= 0; i--)
        {
            var next = tail.Count == 0 ? window[i].Length : length + 1 + window[i].Length;
            if (next > _overlap)
            {
                break;
            }

            tail.Insert(0, window[i]);
            length = next;
        }

        // Never carry the WHOLE window forward. That would repeat a full chunk.
        if (tail.Count == window.Count)
        {
            tail.Clear();
        }

        return tail;
    }

    // Sentences, with any sentence longer than _max cut at word borders.
    private IEnumerable<string> Units(string text)
    {
        foreach (var sentence in SentenceBreak().Split(text))
        {
            var rest = sentence;
            while (rest.Length > _max)
            {
                // Search backward from _max for a space, so we cut between words.
                var cut = rest.LastIndexOf(' ', _max);
                if (cut <= 0)
                {
                    cut = _max; // One giant "word" (a long code or URL). Hard cut.
                }

                yield return rest[..cut];
                rest = rest[cut..].TrimStart();
            }

            if (rest.Length > 0)
            {
                yield return rest;
            }
        }
    }

    private static int JoinedLength(List<string> parts) =>
        parts.Count == 0 ? 0 : parts.Sum(p => p.Length) + parts.Count - 1;

    // A sentence ends at . ! or ? followed by whitespace.
    // Simple on purpose. It splits "e.g. this" wrongly, and that is OK:
    // a few extra cuts do not hurt retrieval.
    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}