namespace RxRag.Core.Labels;

/// <summary>
/// One named section of a drug label, such as "Warnings" or "Drug interactions".
/// </summary>
/// <remarks>
/// We keep the section as a separate type because the section is the unit of
/// meaning on a drug label. A citation like "Advil, Warnings" tells the user
/// where an answer came from. Without the section, the citation is too vague
/// to check.
/// </remarks>
public sealed record LabelSection
{
    /// <summary>Creates a section and checks every value.</summary>
    /// <param name="key">
    /// Machine name of the section. It is the openFDA field name, for example
    /// "drug_interactions". It must be lowercase letters, digits, and
    /// underscores, and start with a letter. It goes into chunk IDs.
    /// </param>
    /// <param name="title">Human name for citations, for example "Drug interactions".</param>
    /// <param name="text">The full section text.</param>
    /// <exception cref="ArgumentException">A value is blank or the key has a bad format.</exception>
    public LabelSection(string key, string title, string text)
    {
        // Fail fast. A bad value found here is easy to trace.
        // The same bad value found inside Azure Search is not.
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (!KeyRules.IsSectionKey(key))
        {
            throw new ArgumentException(
                $"Section key '{key}' must match ^[a-z][a-z0-9_]*$.", nameof(key));
        }

        Key = key;
        Title = title;
        Text = text;
    }

    /// <summary>Machine name, for example "drug_interactions".</summary>
    public string Key { get; }

    /// <summary>Human name, for example "Drug interactions".</summary>
    public string Title { get; }

    /// <summary>The full section text.</summary>
    public string Text { get; }
}