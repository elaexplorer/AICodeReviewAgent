using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// Manages Azure OpenAI chat configuration dynamically (endpoint/key/deployment/api version).
/// </summary>
public class ChatConfigurationService
{
    private readonly ILogger<ChatConfigurationService> _logger;

    private string? _endpoint;
    private string? _apiKey;
    private string _deployment = "gpt-4";
    private string _apiVersion = "2024-02-01";

    public ChatConfigurationService(ILogger<ChatConfigurationService> logger)
    {
        _logger = logger;

        if (IsForceUiConfigEnabled())
        {
            _logger.LogInformation("FORCE_UI_CONFIG enabled: skipping chat environment auto-configuration");
            return;
        }

        _endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        _apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        _deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4";
        _apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2024-02-01";

        if (IsConfigured)
        {
            _logger.LogInformation("Chat configuration loaded from environment variables");
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
            return (false, "Chat endpoint, API key, and deployment are required.");
        }

        try
        {
            _logger.LogInformation("Validating chat configuration for endpoint {Endpoint} and deployment {Deployment}", endpoint, deployment);

            if (endpoint.Contains("anthropic", StringComparison.OrdinalIgnoreCase))
            {
                using var httpClient = new HttpClient();
                var claudeLogger = LoggerFactory.Create(builder => { }).CreateLogger<ClaudeChatClient>();
                var claudeClient = new ClaudeChatClient(httpClient, endpoint, apiKey, deployment, claudeLogger);
                await claudeClient.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Respond with ok") });
            }
            else
            {
                var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
                var chatClient = azureClient.GetChatClient(deployment).AsIChatClient();
                await chatClient.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Respond with ok") });
            }

            _endpoint = endpoint;
            _apiKey = apiKey;
            _deployment = deployment;
            if (!string.IsNullOrWhiteSpace(apiVersion))
            {
                _apiVersion = apiVersion;
            }

            _logger.LogInformation("Chat configuration validated successfully");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat configuration validation failed");
            return (false, $"Chat validation failed: {ex.Message}");
        }
    }

    public void ClearConfiguration()
    {
        _endpoint = null;
        _apiKey = null;
        _deployment = "gpt-4";
        _apiVersion = "2024-02-01";
        _logger.LogInformation("Chat configuration cleared");
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
