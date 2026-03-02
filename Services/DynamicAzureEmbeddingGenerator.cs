using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// Embedding generator that reads Azure OpenAI embedding config dynamically at call time.
/// </summary>
public sealed class DynamicAzureEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly EmbeddingConfigurationService _config;
    private readonly ILogger<DynamicAzureEmbeddingGenerator> _logger;

    public DynamicAzureEmbeddingGenerator(
        EmbeddingConfigurationService config,
        ILogger<DynamicAzureEmbeddingGenerator> logger)
    {
        _config = config;
        _logger = logger;
    }

    public EmbeddingGeneratorMetadata Metadata { get; } = new("DynamicAzureEmbeddingGenerator");

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured ||
            string.IsNullOrWhiteSpace(_config.Endpoint) ||
            string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            throw new InvalidOperationException("Embedding configuration is missing. Configure embedding endpoint/key/deployment in the app settings screen.");
        }

        var azureClient = new AzureOpenAIClient(
            new Uri(_config.Endpoint),
            new AzureKeyCredential(_config.ApiKey));

        var embeddingClient = azureClient.GetEmbeddingClient(_config.Deployment);
        var generator = embeddingClient.AsIEmbeddingGenerator();

        _logger.LogDebug("Using embedding endpoint {Endpoint} and deployment {Deployment}", _config.Endpoint, _config.Deployment);
        return await generator.GenerateAsync(values, options, cancellationToken);
    }

    public TService? GetService<TService>(object? key = null) where TService : class
    {
        return default;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    public void Dispose()
    {
    }
}
