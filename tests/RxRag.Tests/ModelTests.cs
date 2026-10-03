using System.Text.RegularExpressions;
using RxRag.Core.Chunks;
using RxRag.Core.Labels;
using Xunit;

namespace RxRag.Tests;

// Tests for the 3 core models.
// Each test checks one rule, so a failure names the broken rule.
public class ModelTests
{
    private const string SetId = "8ec3d5c4-1b2a-4c3d-9e8f-0a1b2c3d4e5f";

    private static readonly LabelSection Warnings =
        new("warnings", "Warnings", "May cause stomach bleeding.");

    private static readonly LabelSection Interactions =
        new("drug_interactions", "Drug interactions", "Ask a doctor if you take a blood thinner.");

    private static DrugLabel SampleLabel() =>
        new(SetId, "Advil", "ibuprofen", new DateOnly(2024, 1, 15), [Warnings, Interactions]);

    // Azure AI Search document key rule, copied from the Azure naming docs.
    // We test against our own copy of the rule, not the app's copy,
    // so a bug in KeyRules can not hide itself.
    private static readonly Regex AzureKeyRule = new("^[A-Za-z0-9=-][A-Za-z0-9_=-]{0,1023}$");

    // ---- Chunk ----

    [Fact]
    public void Create_BuildsExpectedId()
    {
        var chunk = Chunk.Create(SampleLabel(), Warnings, 0, "text");

        Assert.Equal($"{SetId}_warnings_0", chunk.Id);
    }

    [Fact]
    public void Create_SameInput_GivesSameId()
    {
        // This is what makes re-running ingest safe.
        var a = Chunk.Create(SampleLabel(), Interactions, 3, "text");
        var b = Chunk.Create(SampleLabel(), Interactions, 3, "other text");

        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public void Create_IdIsValidAzureSearchKey()
    {
        var chunk = Chunk.Create(SampleLabel(), Interactions, 12, "text");

        Assert.Matches(AzureKeyRule, chunk.Id);
    }

    [Fact]
    public void Create_CopiesLabelAndSectionNames()
    {
        var chunk = Chunk.Create(SampleLabel(), Interactions, 1, "text");

        Assert.Equal(SetId, chunk.SetId);
        Assert.Equal("Advil", chunk.BrandName);
        Assert.Equal("ibuprofen", chunk.GenericName);
        Assert.Equal("drug_interactions", chunk.SectionKey);
        Assert.Equal("Drug interactions", chunk.SectionTitle);
        Assert.Equal(1, chunk.Ordinal);
    }

    [Fact]
    public void Citation_HasBrandGenericAndSection()
    {
        var chunk = Chunk.Create(SampleLabel(), Warnings, 0, "text");

        Assert.Equal("Advil (ibuprofen), Warnings", chunk.Citation);
    }

    [Fact]
    public void Create_NegativeOrdinal_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Chunk.Create(SampleLabel(), Warnings, -1, "text"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankText_Throws(string text)
    {
        Assert.Throws<ArgumentException>(
            () => Chunk.Create(SampleLabel(), Warnings, 0, text));
    }

    [Fact]
    public void Create_SectionFromOtherLabel_Throws()
    {
        var stranger = new LabelSection("boxed_warning", "Boxed warning", "Other drug text.");

        Assert.Throws<ArgumentException>(
            () => Chunk.Create(SampleLabel(), stranger, 0, "text"));
    }

    // ---- LabelSection ----

    [Theory]
    [InlineData("Warnings")]           // uppercase
    [InlineData("drug interactions")]  // space
    [InlineData("1warnings")]          // starts with digit
    [InlineData("_warnings")]          // starts with underscore
    [InlineData("warnings-2")]         // dash
    public void LabelSection_BadKey_Throws(string key)
    {
        Assert.Throws<ArgumentException>(() => new LabelSection(key, "Title", "Text"));
    }

    // ---- DrugLabel ----

    [Theory]
    [InlineData("abc_def")]   // underscore would break ID uniqueness
    [InlineData("-abc")]      // starts with dash
    [InlineData("has space")]
    public void DrugLabel_BadSetId_Throws(string setId)
    {
        Assert.Throws<ArgumentException>(
            () => new DrugLabel(setId, "Advil", "ibuprofen", default, [Warnings]));
    }

    [Fact]
    public void DrugLabel_NoSections_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new DrugLabel(SetId, "Advil", "ibuprofen", default, []));
    }

    [Fact]
    public void DrugLabel_DuplicateSectionKeys_Throws()
    {
        var copy = new LabelSection("warnings", "Warnings again", "Other text.");

        Assert.Throws<ArgumentException>(
            () => new DrugLabel(SetId, "Advil", "ibuprofen", default, [Warnings, copy]));
    }

    [Fact]
    public void DrugLabel_CopiesSections()
    {
        // Change the caller's list after building the label.
        // The label must not change.
        var list = new List<LabelSection> { Warnings };
        var label = new DrugLabel(SetId, "Advil", "ibuprofen", default, list);

        list.Add(Interactions);

        Assert.Single(label.Sections);
    }
}