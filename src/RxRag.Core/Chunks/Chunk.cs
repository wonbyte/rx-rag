using System.Globalization;
using RxRag.Core.Labels;

namespace RxRag.Core.Chunks;

/// <summary>
/// One small piece of label text. This is the unit we embed, store in the
/// search index, retrieve, and show to the model.
/// </summary>
/// <remarks>
/// Each chunk carries a copy of its label and section names. This is
/// "denormalized" data (copied, not linked). The search index has no joins,
/// so every chunk must hold all it needs to build a citation by itself.
/// </remarks>
public sealed record Chunk
{
    // Private: the only way to make a chunk is Create(). That way every
    // chunk has a correct, stable ID. Nobody can make an ID by hand.
    private Chunk(
        string id,
        string setId,
        string brandName,
        string genericName,
        string sectionKey,
        string sectionTitle,
        int ordinal,
        string text)
    {
        Id = id;
        SetId = setId;
        BrandName = brandName;
        GenericName = genericName;
        SectionKey = sectionKey;
        SectionTitle = sectionTitle;
        Ordinal = ordinal;
        Text = text;
    }

    /// <summary>
    /// Builds a chunk with a stable ID of the form "{setId}_{sectionKey}_{ordinal}".
    /// </summary>
    /// <remarks>
    /// Same input, same ID. Re-running ingest updates chunks in place
    /// instead of adding copies.
    ///
    /// The ID is always unique: the set ID has no "_", and the ordinal is
    /// digits only and comes after the last "_". So two different
    /// (section, ordinal) pairs can not make the same string.
    /// </remarks>
    /// <param name="label">The label the text comes from.</param>
    /// <param name="section">The section the text comes from. Must belong to <paramref name="label"/>.</param>
    /// <param name="ordinal">Zero-based position of this chunk inside its section.</param>
    /// <param name="text">The chunk text.</param>
    /// <returns>A new chunk.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="label"/> or <paramref name="section"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is negative.</exception>
    /// <exception cref="ArgumentException">Text is blank, or the section is not part of the label.</exception>
    public static Chunk Create(DrugLabel label, LabelSection section, int ordinal, string text)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        // Guard against a wrong pairing, for example a section from label A
        // with label B. That bug would make citations point at the wrong drug,
        // which is a safety problem in a pharmacy app.
        if (!label.Sections.Contains(section))
        {
            throw new ArgumentException("Section is not part of this label.", nameof(section));
        }

        // InvariantCulture: the ID must be the same on every machine,
        // no matter the server's language settings.
        var id = string.Create(
            CultureInfo.InvariantCulture,
            $"{label.SetId}_{section.Key}_{ordinal}");

        // The inputs are already checked, so this should never fail.
        // It is a last check before the ID reaches Azure.
        if (!KeyRules.IsSearchKey(id))
        {
            throw new InvalidOperationException($"Chunk ID '{id}' is not a valid search key.");
        }

        return new Chunk(
            id,
            label.SetId,
            label.BrandName,
            label.GenericName,
            section.Key,
            section.Title,
            ordinal,
            text);
    }

    /// <summary>Stable ID, also the search document key.</summary>
    public string Id { get; }

    /// <summary>openFDA set_id of the source label.</summary>
    public string SetId { get; }

    /// <summary>Brand name of the source drug.</summary>
    public string BrandName { get; }

    /// <summary>Generic name of the source drug.</summary>
    public string GenericName { get; }

    /// <summary>Machine name of the source section.</summary>
    public string SectionKey { get; }

    /// <summary>Human name of the source section.</summary>
    public string SectionTitle { get; }

    /// <summary>Zero-based position of this chunk inside its section.</summary>
    public int Ordinal { get; }

    /// <summary>The chunk text.</summary>
    public string Text { get; }

    /// <summary>Short source line for the UI, for example "Advil (ibuprofen), Warnings".</summary>
    public string Citation => $"{BrandName} ({GenericName}), {SectionTitle}";
}