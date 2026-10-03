using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RxRag.Core.Labels;

namespace RxRag.Core.OpenFda;

/// <summary>
/// Reads drug labels from the public openFDA API (https://api.fda.gov).
/// </summary>
/// <remarks>
/// This is a "typed HttpClient". DI gives it an HttpClient that already has
/// the base address and a retry policy (see ServiceCollectionExtensions).
/// The client only knows HTTP and JSON. It returns our own DrugLabel type,
/// so no openFDA JSON shape leaks into the rest of the app.
/// </remarks>
/// <param name="http">HttpClient with BaseAddress set to https://api.fda.gov/.</param>
/// <param name="logger">Logger for labels we skip.</param>
public sealed partial class OpenFdaClient(HttpClient http, ILogger<OpenFdaClient> logger)
{
    /// <summary>Gets up to <paramref name="limit"/> labels for one generic drug name.</summary>
    /// <param name="genericName">Generic name, for example "ibuprofen".</param>
    /// <param name="limit">Max labels, 1 to 100.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <returns>Valid labels. Empty if openFDA has no match.</returns>
    /// <exception cref="ArgumentException">Name is blank or has a double quote.</exception>
    /// <exception cref="HttpRequestException">openFDA failed after retries.</exception>
    public async Task<IReadOnlyList<DrugLabel>> GetLabelsAsync(
        string genericName, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(genericName);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);

        // A double quote would close our quoted search term early and
        // change the query. Reject it instead of trying to escape it.
        if (genericName.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("Name must not contain a double quote.", nameof(genericName));
        }

        // Exact phrase match on the generic name field.
        var search = Uri.EscapeDataString($"openfda.generic_name:\"{genericName}\"");
        var url = string.Create(CultureInfo.InvariantCulture, $"drug/label.json?search={search}&limit={limit}");

        using var response = await http.GetAsync(url, cancellationToken);

        // openFDA answers "no match" with 404, not with an empty list.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return Parse(doc.RootElement);
    }

    // We read the JSON by hand with JsonDocument instead of a C# class,
    // because the section fields are many and optional. A class with 18
    // nullable string[] properties would be longer and harder to read.
    private List<DrugLabel> Parse(JsonElement root)
    {
        var labels = new List<DrugLabel>();
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return labels;
        }

        foreach (var item in results.EnumerateArray())
        {
            var setId = GetString(item, "set_id");
            var brand = FirstOf(item, "openfda", "brand_name");
            var generic = FirstOf(item, "openfda", "generic_name");

            // Some labels have no "openfda" block. Without names we can not
            // build a useful citation, so we skip them.
            if (setId is null || brand is null || generic is null)
            {
                LogSkipped(logger, setId ?? "(none)", "missing set_id or names");
                continue;
            }

            var effective = GetString(item, "effective_time");
            if (effective is null || !DateOnly.TryParseExact(
                    effective, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                LogSkipped(logger, setId, "bad effective_time");
                continue;
            }

            var sections = new List<LabelSection>();
            foreach (var (key, title) in OpenFdaSections.Known)
            {
                if (!item.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                // Each section is an array of strings. Join the parts.
                var text = string.Join("\n", value.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()));

                if (!string.IsNullOrWhiteSpace(text))
                {
                    sections.Add(new LabelSection(key, title, text));
                }
            }

            if (sections.Count == 0)
            {
                LogSkipped(logger, setId, "no known sections");
                continue;
            }

            // DrugLabel checks its own rules (for example the set_id format).
            // One bad label must not stop the whole ingest, so we log and skip.
            try
            {
                labels.Add(new DrugLabel(setId, brand, generic, date, sections));
            }
            catch (ArgumentException ex)
            {
                LogSkipped(logger, setId, ex.Message);
            }
        }

        return labels;
    }

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // First string in item.{obj}.{name}[], or null.
    private static string? FirstOf(JsonElement item, string obj, string name) =>
        item.TryGetProperty(obj, out var o)
        && o.ValueKind == JsonValueKind.Object
        && o.TryGetProperty(name, out var arr)
        && arr.ValueKind == JsonValueKind.Array
        && arr.GetArrayLength() > 0
        && arr[0].ValueKind == JsonValueKind.String
            ? arr[0].GetString()
            : null;

    // [LoggerMessage] makes the compiler write fast logging code.
    // No string building happens if Warning level is off.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped openFDA label {SetId}: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string setId, string reason);
}