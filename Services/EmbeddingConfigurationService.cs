using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// Manages Azure OpenAI embedding configuration dynamically (endpoint/key/deployment/api version).
/// </summary>
public class EmbeddingConfigurationService
{
    private readonly ILogger<EmbeddingConfigurationService> _logger;

    private string? _endpoint;
    private string? _apiKey;
    private string _deployment = "text-embedding-ada-002";
    private string _apiVersion = "2024-02-01";

    public EmbeddingConfigurationService(ILogger<EmbeddingConfigurationService> logger)
    {
        _logger = logger;

        if (IsForceUiConfigEnabled())
        {
            _logger.LogInformation("FORCE_UI_CONFIG enabled: skipping embedding environment auto-configuration");
            return;
        }

        var baseEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var baseApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

        _endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_ENDPOINT") ?? baseEndpoint;
        _apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_API_KEY") ?? baseApiKey;
        _deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") ?? "text-embedding-ada-002";
        _apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2024-02-01";

        if (IsConfigured)
        {
            _logger.LogInformation("Embedding configuration loaded from environment variables");
        }
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_endpoint) &&
        !string.IsNullOrWhiteSpace(_apiKey) &&
        !string.IsNullOrWhiteSpace(_deployment);

    public string? Endpoint => _endpoint;
    public string? ApiKey => _apiKey;
    public string Deployment => _deployment;
    public string ApiVersion => _apiVersion;

    /// <summary>
    /// Validates embedding config by calling the configured embedding deployment.
    /// </summary>
    public async Task<(bool isValid, string? errorMessage)> ValidateAndConfigureAsync(
        string endpoint,
        string apiKey,
        string deployment,
        string? apiVersion)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(deployment))
        {
            return (false, "Embedding endpoint, API key, and deployment are required.");
        }

        try
        {
            _logger.LogInformation("Validating embedding configuration for endpoint {Endpoint} and deployment {Deployment}", endpoint, deployment);

            var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            var embeddingClient = azureClient.GetEmbeddingClient(deployment);
            var generator = embeddingClient.AsIEmbeddingGenerator();

            // Connectivity/auth/deployment validation probe.
            var result = await generator.GenerateAsync(new[] { "embedding validation probe" });
            if (result.Count == 0)
            {
                return (false, "Embedding validation failed: no embeddings returned.");
            }

            _endpoint = endpoint;
            _apiKey = apiKey;
            _deployment = deployment;
            if (!string.IsNullOrWhiteSpace(apiVersion))
            {
                _apiVersion = apiVersion;
            }

            _logger.LogInformation("Embedding configuration validated successfully");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding configuration validation failed");
            return (false, $"Embedding validation failed: {ex.Message}");
        }
    }

    public void ClearConfiguration()
    {
        _endpoint = null;
        _apiKey = null;
        _deployment = "text-embedding-ada-002";
        _apiVersion = "2024-02-01";
        _logger.LogInformation("Embedding configuration cleared");
    }

    private static bool IsForceUiConfigEnabled()
    {
        var value = Environment.GetEnvironmentVariable("FORCE_UI_CONFIG");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
