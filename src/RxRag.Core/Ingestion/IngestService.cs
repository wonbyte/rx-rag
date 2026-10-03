using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RxRag.Core.Chunks;
using RxRag.Core.Configuration;
using RxRag.Core.OpenFda;
using RxRag.Core.Search;

namespace RxRag.Core.Ingestion;

/// <summary>
/// The "get ready" pipeline: fetch labels, chunk, embed, upload.
/// </summary>
/// <remarks>
/// Safe to run again. The synonym map and index are created or updated in
/// place, and chunk IDs are stable, so uploads overwrite. Known limit: if a
/// label section gets SHORTER, its old extra chunks stay. A real system
/// would delete chunks by setId before re-uploading that label.
/// </remarks>
/// <param name="openFda">openFDA client.</param>
/// <param name="chunker">Chunker.</param>
/// <param name="embedder">Embedding model.</param>
/// <param name="indexClient">Index admin client.</param>
/// <param name="searchClient">Document client for our index.</param>
/// <param name="options">Settings.</param>
/// <param name="logger">Logger.</param>
public sealed partial class IngestService(
    OpenFdaClient openFda,
    Chunker chunker,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    SearchIndexClient indexClient,
    SearchClient searchClient,
    IOptions<RxRagOptions> options,
    ILogger<IngestService> logger)
{
    // Embed in batches: one API call per 64 chunks instead of one per chunk.
    // Fewer calls means less latency and fewer rate-limit hits.
    private const int BatchSize = 64;

    /// <summary>Runs the full pipeline for a list of drugs.</summary>
    /// <param name="genericNames">Generic names to fetch.</param>
    /// <param name="labelsPerDrug">Labels to fetch per name.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>Number of chunks uploaded.</returns>
    public async Task<int> RunAsync(
        IEnumerable<string> genericNames, int labelsPerDrug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(genericNames);
        var o = options.Value;

        // Synonym map FIRST: the index refers to it by name, and Azure
        // rejects an index that names a map that does not exist.
        await indexClient.CreateOrUpdateSynonymMapAsync(
            new SynonymMap(DrugClasses.SynonymMapName, DrugClasses.ToSolrSynonyms()),
            cancellationToken: cancellationToken);

        await indexClient.CreateOrUpdateIndexAsync(
            SearchIndexSchema.Build(o.IndexName, o.EmbeddingDimensions), cancellationToken: cancellationToken);

        // One label can come back for two names (combination products).
        // Track set IDs so we do not pay to embed it twice.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;

        foreach (var name in genericNames)
        {
            var labels = await openFda.GetLabelsAsync(name, labelsPerDrug, cancellationToken);
            var chunks = labels.Where(l => seen.Add(l.SetId)).SelectMany(chunker.Split).ToList();

            // Enumerable.Chunk(n) is LINQ's "split into groups of n".
            // Same word as our Chunk type, different thing.
            foreach (var batch in chunks.Chunk(BatchSize))
            {
                var embeddings = await embedder.GenerateAsync(
                    batch.Select(ChunkDocument.EmbeddingText), cancellationToken: cancellationToken);

                var docs = batch.Zip(embeddings, (chunk, e) => ChunkDocument.From(chunk, e.Vector));

                // MergeOrUpload = insert or replace by key. ThrowOnAnyError:
                // a partial failure must fail the run, not hide in a result list.
                await searchClient.MergeOrUploadDocumentsAsync(
                    docs, new IndexDocumentsOptions { ThrowOnAnyError = true }, cancellationToken);

                total += batch.Length;
            }

            LogDrug(logger, name, labels.Count, chunks.Count);
        }

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Drug}: {Labels} labels, {Chunks} chunks")]
    private static partial void LogDrug(ILogger logger, string drug, int labels, int chunks);
}