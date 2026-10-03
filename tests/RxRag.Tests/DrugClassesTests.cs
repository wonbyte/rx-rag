using RxRag.Core.Search;
using Xunit;

namespace RxRag.Tests;

public class DrugClassesTests
{
    [Fact]
    public void FindIn_BrandName_FindsGeneric()
    {
        var drug = Assert.Single(DrugClasses.FindIn("Is Coumadin safe with food?"));

        Assert.Equal("warfarin", drug.Generic);
    }

    [Fact]
    public void FindIn_IgnoresCase()
    {
        Assert.Single(DrugClasses.FindIn("WARFARIN dose?"));
    }

    [Theory]
    [InlineData("warfarins")]   // longer word
    [InlineData("xwarfarin")]   // name inside another word
    [InlineData("What is the capital of France?")]
    public void FindIn_WholeWordsOnly(string text)
    {
        Assert.Empty(DrugClasses.FindIn(text));
    }

    [Fact]
    public void FindIn_ManyDrugs_InOrder_NoRepeats()
    {
        var drugs = DrugClasses.FindIn("Can I take ibuprofen with warfarin, or Advil instead?");

        Assert.Equal(["ibuprofen", "warfarin"], drugs.Select(d => d.Generic));
    }

    [Fact]
    public void ToSource_CitationSaysItIsReferenceData()
    {
        var warfarin = DrugClasses.FindIn("warfarin")[0];

        var source = DrugClasses.ToSource(warfarin);

        Assert.Equal("class_warfarin", source.Id);
        Assert.Equal("rxrag drug class reference (warfarin), Drug class", source.Citation);
        Assert.Contains("blood thinner", source.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Synonyms_OneRulePerName()
    {
        var rules = DrugClasses.ToSolrSynonyms().Split('\n');

        foreach (var d in DrugClasses.All)
        {
            foreach (var name in d.Brands.Prepend(d.Generic))
            {
                Assert.Contains(rules, r => r.StartsWith($"{name} => ", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void Synonyms_BrandKeepsItselfAndAddsGeneric()
    {
        var rules = DrugClasses.ToSolrSynonyms().Split('\n');

        Assert.Contains("coumadin => coumadin, warfarin, anticoagulant, anticoagulation, blood thinner, thinning the blood", rules);
    }

    [Fact]
    public void AllEntries_AreLowercase()
    {
        // Synonym rules and the name index assume lowercase data.
        foreach (var d in DrugClasses.All)
        {
            foreach (var word in d.Brands.Concat(d.Terms).Prepend(d.Generic))
            {
                Assert.Equal(word.ToLowerInvariant(), word);
            }
        }
    }
}