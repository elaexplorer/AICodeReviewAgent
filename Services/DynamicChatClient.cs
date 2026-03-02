using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// Chat client that uses runtime chat configuration from ChatConfigurationService.
/// </summary>
public class DynamicChatClient : IChatClient
{
    private readonly ChatConfigurationService _config;
    private readonly ILogger<DynamicChatClient> _logger;
    private readonly ILogger<ClaudeChatClient> _claudeLogger;

    public DynamicChatClient(
        ChatConfigurationService config,
        ILogger<DynamicChatClient> logger,
        ILogger<ClaudeChatClient> claudeLogger)
    {
        _config = config;
        _logger = logger;
        _claudeLogger = claudeLogger;
    }

    public ChatClientMetadata Metadata => new("DynamicChatClient");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        return await client.GetResponseAsync(chatMessages, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        await foreach (var update in client.GetStreamingResponseAsync(chatMessages, options, cancellationToken))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    public void Dispose()
    {
    }

    private IChatClient CreateClient()
    {
        if (!_config.IsConfigured ||
            string.IsNullOrWhiteSpace(_config.Endpoint) ||
            string.IsNullOrWhiteSpace(_config.ApiKey) ||
            string.IsNullOrWhiteSpace(_config.Deployment))
        {
            throw new InvalidOperationException("Chat configuration is missing. Configure chat endpoint/key/deployment in the login screen.");
        }

        if (_config.Endpoint.Contains("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Using Claude chat endpoint {Endpoint} with deployment {Deployment}", _config.Endpoint, _config.Deployment);
            return new ClaudeChatClient(new HttpClient(), _config.Endpoint, _config.ApiKey, _config.Deployment, _claudeLogger);
        }

        _logger.LogDebug("Using Azure OpenAI chat endpoint {Endpoint} with deployment {Deployment}", _config.Endpoint, _config.Deployment);
        var azureClient = new AzureOpenAIClient(new Uri(_config.Endpoint), new AzureKeyCredential(_config.ApiKey));
        return azureClient.GetChatClient(_config.Deployment).AsIChatClient();
    }
}
