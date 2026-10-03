using System.Text.Json.Serialization;
using RxRag.Core.Chunks;

namespace RxRag.Core.Search;

/// <summary>Search index field names. One place, so the schema and queries can not drift.</summary>
public static class ChunkFields
{
    /// <summary>Document key.</summary>
    public const string Id = "id";
    /// <summary>openFDA set_id.</summary>
    public const string SetId = "setId";
    /// <summary>Brand name.</summary>
    public const string BrandName = "brandName";
    /// <summary>Generic name.</summary>
    public const string GenericName = "genericName";
    /// <summary>Section machine name.</summary>
    public const string SectionKey = "sectionKey";
    /// <summary>Section human name.</summary>
    public const string SectionTitle = "sectionTitle";
    /// <summary>Position in section.</summary>
    public const string Ordinal = "ordinal";
    /// <summary>Chunk text, keyword-searchable.</summary>
    public const string Content = "content";
    /// <summary>Chunk embedding, vector-searchable.</summary>
    public const string ContentVector = "contentVector";
}

/// <summary>
/// The shape of one document in the search index. This is a storage type
/// (a DTO), separate from <see cref="Chunk"/>, so index details like the
/// vector and JSON names stay out of the domain model.
/// </summary>
public sealed class ChunkDocument
{
    /// <summary>Document key (the chunk ID).</summary>
    [JsonPropertyName(ChunkFields.Id)] public string Id { get; set; } = "";
    /// <summary>openFDA set_id.</summary>
    [JsonPropertyName(ChunkFields.SetId)] public string SetId { get; set; } = "";
    /// <summary>Brand name.</summary>
    [JsonPropertyName(ChunkFields.BrandName)] public string BrandName { get; set; } = "";
    /// <summary>Generic name.</summary>
    [JsonPropertyName(ChunkFields.GenericName)] public string GenericName { get; set; } = "";
    /// <summary>Section machine name.</summary>
    [JsonPropertyName(ChunkFields.SectionKey)] public string SectionKey { get; set; } = "";
    /// <summary>Section human name.</summary>
    [JsonPropertyName(ChunkFields.SectionTitle)] public string SectionTitle { get; set; } = "";
    /// <summary>Position in section.</summary>
    [JsonPropertyName(ChunkFields.Ordinal)] public int Ordinal { get; set; }
    /// <summary>Chunk text.</summary>
    [JsonPropertyName(ChunkFields.Content)] public string Content { get; set; } = "";
    /// <summary>Embedding. Null when a query does not select it.</summary>
    [JsonPropertyName(ChunkFields.ContentVector)] public float[]? ContentVector { get; set; }

    /// <summary>Builds an index document from a chunk and its embedding.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <param name="vector">Embedding of <see cref="EmbeddingText"/>.</param>
    /// <returns>A document ready to upload.</returns>
    public static ChunkDocument From(Chunk chunk, ReadOnlyMemory<float> vector)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return new ChunkDocument
        {
            Id = chunk.Id,
            SetId = chunk.SetId,
            BrandName = chunk.BrandName,
            GenericName = chunk.GenericName,
            SectionKey = chunk.SectionKey,
            SectionTitle = chunk.SectionTitle,
            Ordinal = chunk.Ordinal,
            Content = chunk.Text,
            ContentVector = vector.ToArray(),
        };
    }

    /// <summary>
    /// Text we send to the embedding model: a short header plus the chunk.
    /// </summary>
    /// <remarks>
    /// A chunk like "Do not use if you are allergic" says nothing about
    /// WHICH drug. The header ("Advil (ibuprofen) - Do not use: ...") puts the
    /// drug and section into the vector, so a question about ibuprofen finds
    /// it. This trick is called a "contextual chunk header".
    /// </remarks>
    /// <param name="chunk">The chunk.</param>
    /// <returns>Header plus chunk text.</returns>
    public static string EmbeddingText(Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return $"{chunk.BrandName} ({chunk.GenericName}) - {chunk.SectionTitle}: {chunk.Text}";
    }
}