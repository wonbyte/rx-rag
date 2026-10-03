using System.ComponentModel.DataAnnotations;

namespace RxRag.Core.Configuration;

/// <summary>
/// All settings for rxrag, bound from the "RxRag" config section.
/// </summary>
/// <remarks>
/// The [Required] and [Range] attributes run at startup (ValidateOnStart).
/// A missing setting fails the app at boot with a clear message, not later
/// on the first user request with a null reference error.
/// </remarks>
public sealed class RxRagOptions
{
    /// <summary>Config section name.</summary>
    public const string SectionName = "RxRag";

    /// <summary>Azure OpenAI resource endpoint, for example https://name.openai.azure.com/.</summary>
    [Required]
    public Uri? OpenAIEndpoint { get; set; }

    /// <summary>Deployment name of the chat model.</summary>
    [Required]
    public string ChatDeployment { get; set; } = "";

    /// <summary>Deployment name of the embedding model.</summary>
    [Required]
    public string EmbeddingDeployment { get; set; } = "";

    /// <summary>Embedding size. Must match the index. text-embedding-3-small default is 1536.</summary>
    [Range(256, 3072)]
    public int EmbeddingDimensions { get; set; } = 1536;

    /// <summary>Azure AI Search endpoint, for example https://name.search.windows.net.</summary>
    [Required]
    public Uri? SearchEndpoint { get; set; }

    /// <summary>Search index name.</summary>
    [Required]
    public string IndexName { get; set; } = "drug-labels";

    /// <summary>How many chunks to give the model per question.</summary>
    [Range(1, 20)]
    public int TopK { get; set; } = 5;
}