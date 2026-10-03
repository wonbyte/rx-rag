using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RxRag.Core.OpenFda;
using Xunit;

namespace RxRag.Tests;

// We test with a fake HTTP handler. No network. Fast and stable.
public class OpenFdaClientTests
{
    private const string OneLabel = """
    {
      "results": [
        {
          "set_id": "00002127-02bc-4c66-b0c3-ca29d8224afc",
          "effective_time": "20250102",
          "openfda": { "brand_name": ["Advil"], "generic_name": ["IBUPROFEN"] },
          "warnings": ["Stomach bleeding warning.", "Heart attack warning."],
          "drug_interactions": ["Ask a doctor if you take a blood thinner."],
          "spl_product_data_elements": ["noise we ignore"]
        }
      ]
    }
    """;

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static OpenFdaClient Client(FakeHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.fda.gov/") },
            NullLogger<OpenFdaClient>.Instance);

    [Fact]
    public async Task ParsesLabel_KnownSectionsOnly_InKnownOrder()
    {
        var labels = await Client(new FakeHandler(HttpStatusCode.OK, OneLabel)).GetLabelsAsync("ibuprofen", 3);

        var label = Assert.Single(labels);
        Assert.Equal("00002127-02bc-4c66-b0c3-ca29d8224afc", label.SetId);
        Assert.Equal("Advil", label.BrandName);
        Assert.Equal("IBUPROFEN", label.GenericName);
        Assert.Equal(new DateOnly(2025, 1, 2), label.EffectiveDate);
        Assert.Equal(["warnings", "drug_interactions"], label.Sections.Select(s => s.Key));
        Assert.Equal("Stomach bleeding warning.\nHeart attack warning.", label.Sections[0].Text);
    }

    [Fact]
    public async Task BuildsSearchUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, OneLabel);

        await Client(handler).GetLabelsAsync("ibuprofen", 3);

        var url = handler.LastUri!.AbsoluteUri;
        Assert.StartsWith("https://api.fda.gov/drug/label.json?search=", url, StringComparison.Ordinal);
        Assert.Contains("ibuprofen", url, StringComparison.Ordinal);
        Assert.Contains("limit=3", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotFound_ReturnsEmpty()
    {
        var labels = await Client(new FakeHandler(HttpStatusCode.NotFound, "{}")).GetLabelsAsync("nothing", 3);

        Assert.Empty(labels);
    }

    [Fact]
    public async Task ServerError_Throws()
    {
        await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(new FakeHandler(HttpStatusCode.InternalServerError, "")).GetLabelsAsync("ibuprofen", 3));
    }

    [Theory]
    [InlineData("""{ "results": [ { "set_id": "abc", "effective_time": "20250102", "warnings": ["x"] } ] }""")]
    [InlineData("""{ "results": [ { "set_id": "abc", "effective_time": "bad", "openfda": { "brand_name": ["A"], "generic_name": ["B"] }, "warnings": ["x"] } ] }""")]
    [InlineData("""{ "results": [ { "set_id": "abc", "effective_time": "20250102", "openfda": { "brand_name": ["A"], "generic_name": ["B"] } } ] }""")]
    [InlineData("""{ "results": [ { "set_id": "a_b", "effective_time": "20250102", "openfda": { "brand_name": ["A"], "generic_name": ["B"] }, "warnings": ["x"] } ] }""")]
    public async Task BadLabels_AreSkipped(string json)
    {
        // Cases: no openfda block, bad date, no known sections, bad set_id.
        var labels = await Client(new FakeHandler(HttpStatusCode.OK, json)).GetLabelsAsync("x", 3);

        Assert.Empty(labels);
    }

    [Fact]
    public async Task QuoteInName_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Client(new FakeHandler(HttpStatusCode.OK, OneLabel)).GetLabelsAsync("a\"b", 3));
    }
}