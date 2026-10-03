using System.Text.RegularExpressions;

namespace RxRag.Core;

// KeyRules holds the text patterns that our IDs must match.
//
// Why one place: chunk IDs become Azure AI Search document keys. Azure
// rejects bad keys at upload time, which is late and slow to debug.
// We check the same rules here, early, when we build each object.
//
// Why [GeneratedRegex]: the compiler writes the regex code at build time.
// It is faster than new Regex(...) and does no work at startup.
internal static partial class KeyRules
{
    // Azure AI Search limit for a document key length.
    internal const int MaxSearchKeyLength = 1024;

    // Section keys are openFDA field names, for example "drug_interactions".
    // Lowercase letter first, then lowercase letters, digits, or underscores.
    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SectionKeyRegex();

    // openFDA set_id is a GUID string like "8ec3d5c4-1b2a-...".
    // We allow letters, digits, and dashes. No underscore, on purpose:
    // we use "_" as the separator inside chunk IDs.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]*$")]
    private static partial Regex SetIdRegex();

    // Azure document key rule: letters, digits, "-", "_", "=".
    // The first character can not be "_".
    [GeneratedRegex("^[A-Za-z0-9=-][A-Za-z0-9_=-]*$")]
    private static partial Regex SearchKeyRegex();

    internal static bool IsSectionKey(string value) => SectionKeyRegex().IsMatch(value);

    internal static bool IsSetId(string value) => SetIdRegex().IsMatch(value);

    internal static bool IsSearchKey(string value) =>
        value.Length <= MaxSearchKeyLength && SearchKeyRegex().IsMatch(value);
}