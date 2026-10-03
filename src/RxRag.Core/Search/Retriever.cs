using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RxRag.Core.Configuration;

namespace RxRag.Core.Search;

/// <summary>One chunk found for a question.</summary>
/// <param name="Id">Chunk ID.</param>
/// <param name="BrandName">Brand name.</param>
/// <param name="GenericName">Generic name.</param>
/// <param name="SectionTitle">Section human name.</param>
/// <param name="Content">Chunk text.</param>
/// <param name="Score">Search score. Higher is better. Only compare within one result list.</param>
public sealed record RetrievedChunk(
    string Id, string BrandName, string GenericName, string SectionTitle, string Content, double Score)
{
    /// <summary>Source line, for example "Advil (ibuprofen), Warnings".</summary>
    public string Citation => $"{BrandName} ({GenericName}), {SectionTitle}";
}

/// <summary>Finds the chunks that best match a question.</summary>
/// <remarks>
/// An interface so tests and evals can swap in a fake. It is also the seam
/// where you would add a reranker or a second index later.
/// </remarks>
public interface IRetriever
{
    /// <summary>Returns the best chunks, best first.</summary>
    /// <param name="question">User question.</param>
    /// <param name="cancellationToken">Stops the search.</param>
    /// <returns>Up to TopK chunks.</returns>
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hybrid retriever on Azure AI Search: keyword search and vector search
/// in ONE request. Azure merges both lists with Reciprocal Rank Fusion (RRF):
/// a chunk ranked high in either list ends up high in the result.
/// </summary>
/// <remarks>
/// Why hybrid: vectors find meaning ("blood thinner" matches "anticoagulant").
/// Keywords find exact terms (drug names, "Reye's syndrome") that vectors can
/// miss. Each covers the other's weak spot.
/// </remarks>
/// <param name="search">Search client for our index.</param>
/// <param name="embedder">The SAME embedding model used at ingest. A different model gives incompatible vectors.</param>
/// <param name="options">Settings.</param>
public sealed class AzureSearchRetriever(
    SearchClient search,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IOptions<RxRagOptions> options) : IRetriever
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        string question, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var topK = options.Value.TopK;

        var vector = await embedder.GenerateVectorAsync(question, cancellationToken: cancellationToken);

        var searchOptions = new SearchOptions
        {
            Size = topK,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    // Ask the vector side for more candidates than we keep.
                    // RRF then has a bigger pool to fuse with the keyword side.
                    new VectorizedQuery(vector)
                    {
                        KNearestNeighborsCount = topK * 10,
                        Fields = { ChunkFields.ContentVector },
                    },
                },
            },
            // Do not download the vector. It is big and we do not need it.
            Select =
            {
                ChunkFields.Id, ChunkFields.BrandName, ChunkFields.GenericName,
                ChunkFields.SectionTitle, ChunkFields.Content,
            },
        };

        // Passing the question text as well turns on the keyword side.
        var response = await search.SearchAsync<ChunkDocument>(question, searchOptions, cancellationToken);

        var results = new List<RetrievedChunk>(topK);
        await foreach (var hit in response.Value.GetResultsAsync())
        {
            var d = hit.Document;
            results.Add(new RetrievedChunk(d.Id, d.BrandName, d.GenericName, d.SectionTitle, d.Content, hit.Score ?? 0));
        }

        return results;
    }
}