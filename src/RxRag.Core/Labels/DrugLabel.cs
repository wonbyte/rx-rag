namespace RxRag.Core.Labels;

/// <summary>
/// One FDA drug label: who makes the drug, what it is, and its text sections.
/// </summary>
/// <remarks>
/// This is our own domain type. It is NOT the raw openFDA JSON shape.
/// The openFDA client (step 4) maps JSON into this type. That split keeps
/// the rest of the app safe from changes in the outside API.
/// </remarks>
public sealed record DrugLabel
{
    /// <summary>Creates a label and checks every value.</summary>
    /// <param name="setId">
    /// openFDA set_id. It stays the same across label versions, so it is a
    /// good stable base for chunk IDs. Letters, digits, and dashes only.
    /// </param>
    /// <param name="brandName">Brand name, for example "Advil".</param>
    /// <param name="genericName">Generic name, for example "ibuprofen".</param>
    /// <param name="effectiveDate">Date this label version took effect.</param>
    /// <param name="sections">At least one section. Section keys must be unique.</param>
    /// <exception cref="ArgumentException">A value is blank, malformed, or duplicated.</exception>
    public DrugLabel(
        string setId,
        string brandName,
        string genericName,
        DateOnly effectiveDate,
        IEnumerable<LabelSection> sections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);
        ArgumentException.ThrowIfNullOrWhiteSpace(brandName);
        ArgumentException.ThrowIfNullOrWhiteSpace(genericName);
        ArgumentNullException.ThrowIfNull(sections);

        if (!KeyRules.IsSetId(setId))
        {
            throw new ArgumentException(
                $"Set ID '{setId}' must be letters, digits, and dashes, starting with a letter or digit.",
                nameof(setId));
        }

        // Copy the input into a new array. If the caller changes their list
        // later, this label does not change. Immutable data is safe to share
        // across threads and requests.
        LabelSection[] copy = [.. sections];

        if (copy.Length == 0)
        {
            throw new ArgumentException("A label needs at least one section.", nameof(sections));
        }

        // Section keys go into chunk IDs. Two sections with one key would
        // make two different chunks with the same ID, and one would silently
        // overwrite the other in the search index.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in copy)
        {
            if (!seen.Add(section.Key))
            {
                throw new ArgumentException($"Duplicate section key '{section.Key}'.", nameof(sections));
            }
        }

        SetId = setId;
        BrandName = brandName;
        GenericName = genericName;
        EffectiveDate = effectiveDate;
        Sections = copy;
    }

    /// <summary>openFDA set_id. Stable across label versions.</summary>
    public string SetId { get; }

    /// <summary>Brand name, for example "Advil".</summary>
    public string BrandName { get; }

    /// <summary>Generic name, for example "ibuprofen".</summary>
    public string GenericName { get; }

    /// <summary>Date this label version took effect.</summary>
    public DateOnly EffectiveDate { get; }

    /// <summary>The label sections, in source order. Never empty.</summary>
    public IReadOnlyList<LabelSection> Sections { get; }
}