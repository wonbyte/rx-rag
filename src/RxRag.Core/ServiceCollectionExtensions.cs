using System.ClientModel.Primitives;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI;
using RxRag.Core.Answering;
using RxRag.Core.Chunks;
using RxRag.Core.Configuration;
using RxRag.Core.Ingestion;
using RxRag.Core.OpenFda;
using RxRag.Core.Search;

namespace RxRag.Core;

/// <summary>One call wires rxrag into any .NET host: the API, ingest, and evals.</summary>
public static class ServiceCollectionExtensions
{
    // Entra ID scope for Azure OpenAI tokens.
    private const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>Registers all rxrag services.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">App configuration with an "RxRag" section.</param>
    /// <returns>The same service collection, so calls can chain.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IServiceCollection AddRxRag(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind the "RxRag" section and check [Required] / [Range] rules.
        // ValidateOnStart: a hosted app fails at boot if a setting is missing,
        // not on the first user request.
        services.AddOptions<RxRagOptions>()
            .Bind(configuration.GetSection(RxRagOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // DefaultAzureCredential tries, in order: env vars, workload identity
        // (in AKS), managed identity, then your "az login" on a laptop.
        // Same code everywhere, and no API keys to leak or rotate.
        // Singleton: it caches tokens, so one shared instance is cheaper.
        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

        // OPENAI001: the OpenAI SDK marks the constructors that take an
        // AuthenticationPolicy as experimental (they may change in a future
        // version). They are the exact pattern Microsoft documents for the
        // Azure OpenAI v1 endpoint with Entra ID, so we accept the risk.
        // The suppression covers ONLY these two registrations. If an SDK
        // update changes the constructors, the build fails here, in one place.
#pragma warning disable OPENAI001

        // Azure OpenAI v1 endpoint with the plain OpenAI SDK, then wrapped
        // as IChatClient (Microsoft.Extensions.AI). App code only sees
        // IChatClient, so swapping the model provider is a one-line change.
        services.AddChatClient(sp =>
            {
                var o = GetOptions(sp);
                return new OpenAI.Chat.ChatClient(
                        model: o.ChatDeployment,
                        authenticationPolicy: AzurePolicy(sp),
                        options: OpenAIOptions(o))
                    .AsIChatClient();
            })
            // Logs prompts only at Trace level. Keep Trace OFF in production:
            // prompts can hold health questions.
            .UseLogging()
            // Traces and metrics (tokens, latency) via OpenTelemetry.
            // Prompt text is NOT recorded unless you opt in.
            .UseOpenTelemetry();

        // Same pattern for embeddings. We pass the dimensions so the vectors
        // always match the index, even if the model's default changes.
        services.AddEmbeddingGenerator(sp =>
            {
                var o = GetOptions(sp);
                return new OpenAI.Embeddings.EmbeddingClient(
                        model: o.EmbeddingDeployment,
                        authenticationPolicy: AzurePolicy(sp),
                        options: OpenAIOptions(o))
                    .AsIEmbeddingGenerator(o.EmbeddingDimensions);
            })
            .UseOpenTelemetry();

#pragma warning restore OPENAI001

        // Search clients are thread-safe and hold connection pools,
        // so one instance for the whole app is the right lifetime.
        services.AddSingleton(sp => new SearchIndexClient(
            GetOptions(sp).SearchEndpoint!, sp.GetRequiredService<TokenCredential>()));
        services.AddSingleton(sp =>
            sp.GetRequiredService<SearchIndexClient>().GetSearchClient(GetOptions(sp).IndexName));

        services.AddSingleton<IRetriever, AzureSearchRetriever>();
        services.AddSingleton<AnswerService>();
        services.AddSingleton(new Chunker(new ChunkerOptions()));

        // Typed HttpClient for openFDA. The standard resilience handler adds
        // retries with backoff, timeouts, and a circuit breaker (built on
        // Polly), because openFDA sometimes returns 429 or 5xx.
        services.AddHttpClient<OpenFdaClient>(c => c.BaseAddress = new Uri("https://api.fda.gov/"))
            .AddStandardResilienceHandler();

        services.AddTransient<IngestService>();
        return services;
    }

    private static RxRagOptions GetOptions(IServiceProvider sp) =>
        sp.GetRequiredService<IOptions<RxRagOptions>>().Value;

    // Adds an Entra ID bearer token to every request to Azure OpenAI.
    private static BearerTokenPolicy AzurePolicy(IServiceProvider sp) =>
        new(sp.GetRequiredService<TokenCredential>(), CognitiveServicesScope);

    // Resource endpoint + "openai/v1/", for example
    private static OpenAIClientOptions OpenAIOptions(RxRagOptions o) =>
        new() { Endpoint = new Uri(o.OpenAIEndpoint!, "openai/v1/") };
}