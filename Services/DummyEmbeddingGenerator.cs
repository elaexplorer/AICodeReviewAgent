using Microsoft.Extensions.AI;

namespace CodeReviewAgent.Services;

/// <summary>
/// Dummy embedding generator for when Claude is used (Claude doesn't provide embeddings)
/// This disables RAG functionality but allows the application to work
/// </summary>
public class DummyEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata => new("DummyEmbedding", new Uri("http://localhost"), "dummy");

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, 
        EmbeddingGenerationOptions? options = null, 
        CancellationToken cancellationToken = default)
    {
        // Return dummy embeddings (empty vectors)
        var embeddings = values.Select(v => new Embedding<float>(new float[1536])).ToList(); // 1536 is common dimension
        return new GeneratedEmbeddings<Embedding<float>>(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null; // No additional services provided
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}