using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using CodeReviewAgent.Services;

namespace CodeReviewAgent.Services;

/// <summary>
/// Inspect what's stored in memory during codebase processing
/// </summary>
public static class MemoryInspectionExtensions
{
    public static IServiceCollection AddMemoryInspection(this IServiceCollection services)
    {
        services.AddSingleton<MemoryInspector>();
        return services;
    }

    public static async Task RunMemoryInspectionAsync(IServiceProvider services, string[] args)
    {
        var inspector = services.GetRequiredService<MemoryInspector>();
        await inspector.InspectMemoryAsync(args);
    }
}

public class MemoryInspector
{
    private readonly CodebaseContextService _contextService;
    private readonly AzureDevOpsRestClient _adoClient;
    private readonly ILogger<MemoryInspector> _logger;

    public MemoryInspector(
        CodebaseContextService contextService,
        AzureDevOpsRestClient adoClient,
        ILogger<MemoryInspector> logger)
    {
        _contextService = contextService;
        _adoClient = adoClient;
        _logger = logger;
    }

    public async Task InspectMemoryAsync(string[] args)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                         MEMORY INSPECTOR                           ║");
        Console.WriteLine("║            Understanding Codebase Chunks in Memory                ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Parse arguments for repository info
        string project = "SCC";
        string repository = "CodReviewAIAgent";
        
        if (args.Contains("--project"))
        {
            var projectIndex = Array.IndexOf(args, "--project");
            if (projectIndex + 1 < args.Length)
                project = args[projectIndex + 1];
        }
        
        if (args.Contains("--repo"))
        {
            var repoIndex = Array.IndexOf(args, "--repo");
            if (repoIndex + 1 < args.Length)
                repository = args[repoIndex + 1];
        }

        Console.WriteLine($"🎯 Target Repository: {project}/{repository}");
        Console.WriteLine();

        // Step 1: Show current memory state
        await ShowCurrentMemoryState(repository);

        // Step 2: If not indexed, offer to index
        if (!_contextService.IsRepositoryIndexed(repository))
        {
            Console.WriteLine("📚 Repository is not indexed. Indexing now...");
            var chunksCreated = await _contextService.IndexRepositoryAsync(project, repository);
            Console.WriteLine($"✅ Created {chunksCreated} chunks in memory");
            Console.WriteLine();
        }

        // Step 3: Show detailed memory contents
        await ShowDetailedMemoryContents(repository);

        // Step 4: Demonstrate the flow
        await DemonstrateSearchFlow(repository);

        Console.WriteLine("✅ Memory inspection completed!");
    }

    private async Task ShowCurrentMemoryState(string repositoryId)
    {
        Console.WriteLine("🧠 CURRENT MEMORY STATE:");
        Console.WriteLine("═════════════════════════");
        
        var isIndexed = _contextService.IsRepositoryIndexed(repositoryId);
        var chunkCount = _contextService.GetChunkCount(repositoryId);
        
        Console.WriteLine($"Repository ID: {repositoryId}");
        Console.WriteLine($"Is Indexed: {(isIndexed ? "✅ YES" : "❌ NO")}");
        Console.WriteLine($"Chunks in Memory: {chunkCount}");
        
        if (isIndexed)
        {
            var summary = _contextService.GetIndexSummary(repositoryId);
            Console.WriteLine();
            Console.WriteLine("📊 INDEX SUMMARY:");
            Console.WriteLine(summary);
        }
        Console.WriteLine();
    }

    private async Task ShowDetailedMemoryContents(string repositoryId)
    {
        Console.WriteLine("🔍 DETAILED MEMORY CONTENTS:");
        Console.WriteLine("════════════════════════════");
        
        // Access the private field using reflection to show memory contents
        var contextServiceType = _contextService.GetType();
        var inMemoryStoreField = contextServiceType.GetField("_inMemoryStore", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        if (inMemoryStoreField?.GetValue(_contextService) is Dictionary<string, List<CodeChunk>> store)
        {
            Console.WriteLine($"🗃️  STORAGE STRUCTURE:");
            Console.WriteLine($"   Type: Dictionary<string, List<CodeChunk>>");
            Console.WriteLine($"   Total repositories in memory: {store.Count}");
            
            foreach (var kvp in store)
            {
                Console.WriteLine($"   Repository Key: '{kvp.Key}' → {kvp.Value.Count} chunks");
            }
            Console.WriteLine();

            if (store.TryGetValue(repositoryId, out var chunks) && chunks.Any())
            {
                Console.WriteLine($"📋 CHUNKS BREAKDOWN FOR '{repositoryId}':");
                Console.WriteLine($"   Total chunks: {chunks.Count}");
                
                // Group by file
                var fileGroups = chunks.GroupBy(c => c.FilePath).ToList();
                Console.WriteLine($"   Files represented: {fileGroups.Count}");
                Console.WriteLine();
                
                Console.WriteLine("📄 FILES AND THEIR CHUNKS:");
                foreach (var fileGroup in fileGroups.Take(10)) // Show first 10 files
                {
                    Console.WriteLine($"   📁 {fileGroup.Key}");
                    foreach (var chunk in fileGroup.Take(3)) // Show first 3 chunks per file
                    {
                        Console.WriteLine($"      🧩 Chunk {chunk.ChunkIndex}: Lines {chunk.StartLine}-{chunk.EndLine}");
                        Console.WriteLine($"         Vector: {chunk.Embedding.Length} dimensions");
                        Console.WriteLine($"         Content: {chunk.Content.Length} chars");
                        
                        // Show a preview of the content
                        var preview = chunk.Content.Length > 100 
                            ? chunk.Content.Substring(0, 100).Replace("\n", "\\n") + "..."
                            : chunk.Content.Replace("\n", "\\n");
                        Console.WriteLine($"         Preview: \"{preview}\"");
                        
                        // Show embedding sample
                        if (chunk.Embedding.Length > 0)
                        {
                            var embeddingSample = string.Join(", ", 
                                chunk.Embedding.Take(5).Select(v => v.ToString("F3")));
                            Console.WriteLine($"         Embedding: [{embeddingSample}...]");
                        }
                        Console.WriteLine();
                    }
                    
                    if (fileGroup.Count() > 3)
                    {
                        Console.WriteLine($"      ... and {fileGroup.Count() - 3} more chunks");
                        Console.WriteLine();
                    }
                }
                
                if (fileGroups.Count > 10)
                {
                    Console.WriteLine($"   ... and {fileGroups.Count - 10} more files");
                }
            }
        }
        else
        {
            Console.WriteLine("❌ Could not access internal memory store via reflection");
        }
        Console.WriteLine();
    }

    private async Task DemonstrateSearchFlow(string repositoryId)
    {
        Console.WriteLine("🔍 SEARCH FLOW DEMONSTRATION:");
        Console.WriteLine("═════════════════════════════");
        
        // Create a sample query
        var searchQueries = new[]
        {
            "authentication login user",
            "database connection query",
            "HTTP request client",
            "async await task"
        };

        Console.WriteLine("Let's see how the memory search works with sample queries:");
        Console.WriteLine();

        foreach (var query in searchQueries)
        {
            Console.WriteLine($"🔍 Query: \"{query}\"");
            Console.WriteLine("   Flow:");
            Console.WriteLine("   1. Query → Embedding Generator → Query Vector (e.g., 3072 dimensions)");
            Console.WriteLine("   2. Query Vector → Compare against all chunks in memory");
            Console.WriteLine("   3. Calculate cosine similarity for each chunk");
            Console.WriteLine("   4. Rank by similarity score");
            Console.WriteLine("   5. Return top matches above threshold (0.7)");
            
            // We can't easily demonstrate the actual search without creating a full PR file,
            // but we can show the memory structure it would search against
            var summary = _contextService.GetIndexSummary(repositoryId);
            var chunkCount = _contextService.GetChunkCount(repositoryId);
            
            Console.WriteLine($"   Would search against: {chunkCount} chunks in memory");
            Console.WriteLine("   ─────────────────────────");
            Console.WriteLine();
        }

        Console.WriteLine("💾 MEMORY LIFECYCLE:");
        Console.WriteLine("1. IndexRepositoryAsync() → Fetch files from ADO");
        Console.WriteLine("2. SplitIntoChunks() → Create 100-line chunks with 10-line overlap");
        Console.WriteLine("3. EmbeddingGenerator.GenerateAsync() → Generate vector for each chunk");
        Console.WriteLine("4. Store in _inMemoryStore[repositoryId] = List<CodeChunk>");
        Console.WriteLine("5. GetRelevantContextAsync() → Search chunks using cosine similarity");
        Console.WriteLine("6. Return top 5 matches above 0.7 similarity threshold");
        Console.WriteLine();

        Console.WriteLine("📊 MEMORY CHARACTERISTICS:");
        var totalChunks = _contextService.GetChunkCount(repositoryId);
        if (totalChunks > 0)
        {
            // Estimate memory usage
            var estimatedMemoryMB = (totalChunks * 3072 * sizeof(float)) / (1024 * 1024);
            Console.WriteLine($"   Chunks: {totalChunks}");
            Console.WriteLine($"   Vector dimensions: 3072 (text-embedding-3-large)");
            Console.WriteLine($"   Estimated memory: ~{estimatedMemoryMB:F1} MB (vectors only)");
            Console.WriteLine($"   Storage type: In-memory Dictionary (no persistence)");
            Console.WriteLine($"   Lifetime: Until application restart");
        }
        Console.WriteLine();
    }
}