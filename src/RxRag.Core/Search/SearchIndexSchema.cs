using Azure.Search.Documents.Indexes.Models;

namespace RxRag.Core.Search;

/// <summary>Builds the search index definition. The index is code, kept in git, reviewed like code.</summary>
public static class SearchIndexSchema
{
    private const string HnswConfig = "hnsw";
    private const string VectorProfile = "vector-profile";

    /// <summary>Returns the index definition.</summary>
    /// <param name="indexName">Index name.</param>
    /// <param name="dimensions">Embedding size. Must match the embedding model.</param>
    /// <returns>The index.</returns>
    public static SearchIndex Build(string indexName, int dimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

        // HNSW is a graph that finds "nearest" vectors fast without
        // comparing against every document. A profile names which
        // algorithm a vector field uses.
        var vectorSearch = new VectorSearch();
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration(HnswConfig));
        vectorSearch.Profiles.Add(new VectorSearchProfile(VectorProfile, HnswConfig));

        return new SearchIndex(indexName)
        {
            Fields =
            {
                new SimpleField(ChunkFields.Id, SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SimpleField(ChunkFields.SetId, SearchFieldDataType.String) { IsFilterable = true },

                // Searchable: a question that says "Advil" matches by keyword too.
                new SearchableField(ChunkFields.BrandName) { IsFilterable = true },
                new SearchableField(ChunkFields.GenericName) { IsFilterable = true },

                new SimpleField(ChunkFields.SectionKey, SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField(ChunkFields.SectionTitle, SearchFieldDataType.String),
                new SimpleField(ChunkFields.Ordinal, SearchFieldDataType.Int32) { IsSortable = true },

                // English analyzer: "bleeding" also matches "bleed".
                new SearchableField(ChunkFields.Content) { AnalyzerName = LexicalAnalyzerName.EnLucene },

                new SearchField(ChunkFields.ContentVector, SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    VectorSearchDimensions = dimensions,
                    VectorSearchProfileName = VectorProfile,
                },
            },
            VectorSearch = vectorSearch,
        };
    }
}