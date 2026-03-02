using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CodeReviewAgent.Tests.TestFixtures;

public class MockChatClient : IChatClient
{
    private readonly List<string> _responses;
    private int _currentResponseIndex = 0;

    public MockChatClient(params string[] responses)
    {
        _responses = responses?.ToList() ?? new List<string>();
        ChatClientMetadata = new ChatClientMetadata("MockChatClient");
    }

    public ChatClientMetadata ChatClientMetadata { get; }

    public TService? GetService<TService>(object? key = null) where TService : class
    {
        return default(TService);
    }

    public void Dispose()
    {
        // No cleanup needed for mock
    }

    public Task<ChatCompletion> CompleteAsync(
        IList<ChatMessage> chatMessages, 
        ChatOptions? options = null, 
        CancellationToken cancellationToken = default)
    {
        var response = GetNextResponse();
        var completion = new ChatCompletion(new ChatMessage[]
        {
            new ChatMessage(ChatRole.Assistant, response)
        });
        
        return Task.FromResult(completion);
    }

    public async IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteStreamingAsync(
        IList<ChatMessage> chatMessages, 
        ChatOptions? options = null, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = GetNextResponse();
        var chunks = response.Split(' ');
        
        foreach (var chunk in chunks)
        {
            yield return new StreamingChatCompletionUpdate
            {
                Role = ChatRole.Assistant,
                Text = chunk + " "
            };
            await Task.Delay(10, cancellationToken); // Simulate streaming delay
        }
    }

    private string GetNextResponse()
    {
        if (_responses.Count == 0)
        {
            return GenerateDefaultCodeReviewResponse();
        }

        var response = _responses[_currentResponseIndex % _responses.Count];
        _currentResponseIndex++;
        return response;
    }

    private string GenerateDefaultCodeReviewResponse()
    {
        return @"## Code Review Comments

### ✅ **Positive Aspects:**
- Well-structured authentication implementation
- Proper use of dependency injection
- Clear separation of concerns

### ⚠️ **Areas for Improvement:**

**Security Issues:**
1. **Hardcoded credentials** in AuthController.Login method - this should use a proper user validation service
2. **Secret key handling** - ensure JWT secret key is stored securely (e.g., Azure Key Vault, environment variables)

**Code Quality:**
3. **Missing input validation** - validate LoginRequest properties for null/empty values
4. **Error handling** - add try-catch blocks around JWT token generation
5. **Logging** - add structured logging for security events (login attempts, failures)

**Testing:**
6. **Good test coverage** - comprehensive test cases for both success and failure scenarios
7. **Consider edge cases** - test with malformed JSON, missing fields, etc.

### 🔧 **Recommendations:**
- Implement proper user authentication service
- Add input validation attributes
- Use IConfiguration for JWT settings
- Add comprehensive error handling
- Consider implementing rate limiting for login endpoint

Overall, this is a solid foundation for JWT authentication with room for security improvements.";
    }
}

public class MockEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata { get; } = new("MockEmbeddingGenerator");

    public TService? GetService<TService>(object? key = null) where TService : class
    {
        return default(TService);
    }

    public void Dispose()
    {
        // No cleanup needed for mock
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, 
        EmbeddingGenerationOptions? options = null, 
        CancellationToken cancellationToken = default)
    {
        var embeddings = values.Select(value => 
        {
            // Generate a simple mock embedding based on string hash
            var hash = value.GetHashCode();
            var vector = new float[1536]; // Standard embedding size
            
            // Fill with deterministic values based on input
            var random = new Random(hash);
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(random.NextDouble() * 2.0 - 1.0); // Values between -1 and 1
            }
            
            return new Embedding<float>(vector);
        });

        await Task.Delay(10, cancellationToken); // Simulate async work
        return new GeneratedEmbeddings<Embedding<float>>(embeddings);
    }
}