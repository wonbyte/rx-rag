using System.Text.RegularExpressions;

namespace RxRag.Core.Search;

/// <summary>One drug and the class words people and labels use for it.</summary>
/// <param name="Generic">Generic name, lowercase, for example "warfarin".</param>
/// <param name="Brands">Common brand names, lowercase.</param>
/// <param name="Terms">Class words and phrases that labels use, lowercase.</param>
/// <param name="Fact">One plain sentence the model may cite.</param>
public sealed record DrugClass(string Generic, IReadOnlyList<string> Brands, IReadOnlyList<string> Terms, string Fact);

/// <summary>A small curated table that links drug names to their drug class.</summary>
/// <remarks>
/// Labels often name a CLASS, not a drug. The ibuprofen label says "ask a
/// doctor if you take a prescription drug for anticoagulation", never
/// "warfarin". So a question about "ibuprofen with warfarin" missed.
///
/// One table, two uses, so they can never disagree:
/// 1. Search: a synonym map expands "warfarin" to "anticoagulant",
///    "blood thinner", and so on, so keyword search finds that label text.
/// 2. Prompt: the drug's Fact is added as a cited source, so the model may
///    link the two without outside knowledge, and the user sees the link.
///
/// This is reviewed reference data, not FDA label text. The citation says so.
/// </remarks>
public static class DrugClasses
{
    /// <summary>Name of the synonym map in Azure AI Search.</summary>
    public const string SynonymMapName = "drug-classes";

    /// <summary>Source name shown in citations for class facts.</summary>
    public const string SourceName = "rxrag drug class reference";

    /// <summary>All entries. Covers the default ingest drug list.</summary>
    // Declared FIRST: static fields initialize in file order, and the two
    // fields below are built from this list.
    public static IReadOnlyList<DrugClass> All { get; } =
    [
        new("ibuprofen", ["advil", "motrin"],
            ["nsaid", "nonsteroidal anti-inflammatory", "pain reliever", "fever reducer"],
            "Ibuprofen is a nonsteroidal anti-inflammatory drug (NSAID). It is a pain reliever and fever reducer."),
        new("naproxen", ["aleve"],
            ["nsaid", "nonsteroidal anti-inflammatory", "pain reliever", "fever reducer"],
            "Naproxen is a nonsteroidal anti-inflammatory drug (NSAID). It is a pain reliever and fever reducer."),
        new("aspirin", ["ecotrin"],
            ["nsaid", "salicylate", "pain reliever", "fever reducer"],
            "Aspirin is a salicylate and a nonsteroidal anti-inflammatory drug (NSAID). It is a pain reliever and fever reducer."),
        new("acetaminophen", ["tylenol", "paracetamol"],
            ["pain reliever", "fever reducer"],
            "Acetaminophen is a pain reliever and fever reducer. It is not an NSAID."),
        new("diphenhydramine", ["benadryl"],
            ["antihistamine", "sleep aid"],
            "Diphenhydramine is an antihistamine. It is also used as a nighttime sleep aid."),
        new("loratadine", ["claritin"],
            ["antihistamine"],
            "Loratadine is an antihistamine."),
        new("cetirizine", ["zyrtec"],
            ["antihistamine"],
            "Cetirizine is an antihistamine."),
        new("omeprazole", ["prilosec"],
            ["proton pump inhibitor", "ppi", "acid reducer"],
            "Omeprazole is a proton pump inhibitor (PPI), a type of acid reducer."),
        new("famotidine", ["pepcid"],
            ["h2 blocker", "h2 receptor antagonist", "acid reducer"],
            "Famotidine is an H2 blocker, a type of acid reducer."),
        new("warfarin", ["coumadin", "jantoven"],
            ["anticoagulant", "anticoagulation", "blood thinner", "thinning the blood"],
            "Warfarin is an anticoagulant, also called a blood thinner."),
        new("metformin", ["glucophage"],
            ["biguanide", "diabetes medicine"],
            "Metformin is a medicine for type 2 diabetes. It is a biguanide."),
        new("lisinopril", ["prinivil", "zestril"],
            ["ace inhibitor", "blood pressure medicine"],
            "Lisinopril is an ACE inhibitor, a medicine for high blood pressure."),
        new("atorvastatin", ["lipitor"],
            ["statin", "cholesterol medicine"],
            "Atorvastatin is a statin, a medicine that lowers cholesterol."),
    ];

    // Every generic and brand name, mapped to its entry. Case-insensitive,
    // so "Coumadin" and "COUMADIN" both find warfarin. Dictionary.Add throws
    // on a duplicate name, so one name can never point at two drugs.
    private static readonly Dictionary<string, DrugClass> ByName = BuildIndex();

    // One regex with every name as a whole word: "warfarin" matches,
    // "warfarins" does not. Longer names first, so a long name wins over
    // a shorter name inside it.
    private static readonly Regex NamePattern = new(
        @"\b(?:" + string.Join("|", ByName.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Finds the drugs a text names, by generic or brand name.</summary>
    /// <param name="text">Usually the user's question.</param>
    /// <returns>Matching entries, no repeats, in order of first mention.</returns>
    public static IReadOnlyList<DrugClass> FindIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var found = new List<DrugClass>();
        foreach (Match m in NamePattern.Matches(text))
        {
            var drug = ByName[m.Value];
            if (!found.Contains(drug))
            {
                found.Add(drug);
            }
        }

        return found;
    }

    /// <summary>Turns an entry into a source the prompt can number and cite.</summary>
    /// <param name="drug">The entry.</param>
    /// <returns>A source whose citation reads "rxrag drug class reference (warfarin), Drug class".</returns>
    public static RetrievedChunk ToSource(DrugClass drug)
    {
        ArgumentNullException.ThrowIfNull(drug);
        return new RetrievedChunk($"class_{drug.Generic}", SourceName, drug.Generic, "Drug class", drug.Fact, 0);
    }

    /// <summary>Builds the synonym rules in Solr format, one rule per name.</summary>
    /// <remarks>
    /// "=>" is a one-way rule: "coumadin" expands to the class words, but
    /// "blood thinner" does NOT expand to "coumadin". A question about blood
    /// thinners in general should not pull in only warfarin labels.
    /// The left-hand name is repeated on the right, or the query would lose it.
    /// </remarks>
    /// <returns>Rules such as "coumadin => coumadin, warfarin, anticoagulant, ...".</returns>
    public static string ToSolrSynonyms()
    {
        var lines = new List<string>();
        foreach (var d in All)
        {
            var expansion = string.Join(", ", d.Terms.Prepend(d.Generic));
            foreach (var name in d.Brands.Prepend(d.Generic))
            {
                var rhs = name == d.Generic ? expansion : $"{name}, {expansion}";
                lines.Add($"{name} => {rhs}");
            }
        }

        return string.Join('\n', lines);
    }

    private static Dictionary<string, DrugClass> BuildIndex()
    {
        var index = new Dictionary<string, DrugClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in All)
        {
            foreach (var name in d.Brands.Prepend(d.Generic))
            {
                index.Add(name, d);
            }
        }

        return index;
    }
}