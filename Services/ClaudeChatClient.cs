using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeReviewAgent.Services;

/// <summary>
/// Custom IChatClient implementation for Claude API via Azure
/// </summary>
public class ClaudeChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<ClaudeChatClient> _logger;

    public ClaudeChatClient(
        HttpClient httpClient,
        string endpoint,
        string apiKey,
        string model,
        ILogger<ClaudeChatClient> logger)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _apiKey = apiKey;
        _model = model;
        _logger = logger;

        // Configure HTTP client
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public ChatClientMetadata Metadata => new("Claude", new Uri(_endpoint), _model);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages, 
        ChatOptions? options = null, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Sending request to Claude API: {Endpoint}", _endpoint);

        // Convert ChatMessages to Claude format
        var claudeRequest = ConvertToClaudeRequest(chatMessages.ToList(), options);
        
        _logger.LogDebug("Claude request: {Request}", JsonSerializer.Serialize(claudeRequest));

        try
        {
            _logger.LogDebug("Sending HTTP request to Claude API...");
            
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            httpRequest.Content = JsonContent.Create(claudeRequest);
            
            // Set timeout for Claude API calls (30 seconds)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
            
            var response = await _httpClient.SendAsync(httpRequest, combined.Token);
            
            _logger.LogDebug("Received HTTP response from Claude API: {StatusCode}", response.StatusCode);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(combined.Token);
                _logger.LogError("Claude API error {StatusCode}: {Error}", response.StatusCode, errorContent);
                throw new HttpRequestException($"Claude API error {response.StatusCode}: {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync(combined.Token);
            _logger.LogDebug("Claude API raw response: {Response}", responseContent);
            
            var claudeResponse = JsonSerializer.Deserialize<ClaudeResponse>(responseContent);
            _logger.LogDebug("Claude response parsed successfully");

            return ConvertFromClaudeResponse(claudeResponse!);
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Claude API request timed out after 30 seconds");
            throw new TimeoutException("Claude API request timed out", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Claude API: {Message}", ex.Message);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // For simplicity, fall back to non-streaming
        var response = await GetResponseAsync(chatMessages, options, cancellationToken);
        
        yield return new Microsoft.Extensions.AI.ChatResponseUpdate
        {
            FinishReason = response.FinishReason
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null; // No additional services provided
    }

    public void Dispose()
    {
        // HttpClient is managed externally, don't dispose it
    }

    private ClaudeRequest ConvertToClaudeRequest(IList<ChatMessage> chatMessages, ChatOptions? options)
    {
        var messages = new List<ClaudeMessage>();
        string? systemMessage = null;

        foreach (var msg in chatMessages)
        {
            if (msg.Role == ChatRole.System)
            {
                systemMessage = msg.Text;
            }
            else if (msg.Role == ChatRole.User)
            {
                messages.Add(new ClaudeMessage { Role = "user", Content = msg.Text ?? "" });
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                messages.Add(new ClaudeMessage { Role = "assistant", Content = msg.Text ?? "" });
            }
        }

        var request = new ClaudeRequest
        {
            Model = _model,
            MaxTokens = options?.MaxOutputTokens ?? 4096,
            Messages = messages
        };

        if (!string.IsNullOrEmpty(systemMessage))
        {
            request.System = systemMessage;
        }

        return request;
    }

    private ChatResponse ConvertFromClaudeResponse(ClaudeResponse response)
    {
        var content = response.Content?.FirstOrDefault();
        var text = content?.Text ?? "";

        var usage = new UsageDetails
        {
            InputTokenCount = response.Usage?.InputTokens,
            OutputTokenCount = response.Usage?.OutputTokens,
            TotalTokenCount = (response.Usage?.InputTokens ?? 0) + (response.Usage?.OutputTokens ?? 0)
        };

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = response.StopReason switch
            {
                "end_turn" => ChatFinishReason.Stop,
                "max_tokens" => ChatFinishReason.Length,
                "stop_sequence" => ChatFinishReason.Stop,
                _ => ChatFinishReason.Stop
            },
            Usage = usage,
            ModelId = response.Model
        };
    }

    // Claude API DTOs
    private class ClaudeRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }

        [JsonPropertyName("messages")]
        public List<ClaudeMessage> Messages { get; set; } = new();

        [JsonPropertyName("system")]
        public string? System { get; set; }
    }

    private class ClaudeMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }

    private class ClaudeResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("content")]
        public List<ClaudeContent>? Content { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; set; }

        [JsonPropertyName("usage")]
        public ClaudeUsage? Usage { get; set; }
    }

    private class ClaudeContent
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private class ClaudeUsage
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }
    }
}