using System.Text;
using System.Text.RegularExpressions;
using CodeReviewAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Embeddings;

namespace CodeReviewAgent.Services;

/// <summary>
/// Provides intelligent codebase context using RAG (Retrieval Augmented Generation)
/// </summary>
public class CodebaseContextService
{
    private readonly ITextEmbeddingGenerationService _embeddingService;
    private readonly AzureDevOpsRestClient _adoClient;
    private readonly ILogger<CodebaseContextService> _logger;
    private readonly Dictionary<string, List<CodeChunk>> _inMemoryStore;
    private const string COLLECTION_NAME = "codebase";

    public CodebaseContextService(
        ITextEmbeddingGenerationService embeddingService,
        AzureDevOpsRestClient adoClient,
        ILogger<CodebaseContextService> logger)
    {
        _embeddingService = embeddingService;
        _adoClient = adoClient;
        _logger = logger;
        _inMemoryStore = new Dictionary<string, List<CodeChunk>>();
    }

    /// <summary>
    /// Index the entire repository for semantic search
    /// Run this once when PR is opened, or periodically
    /// </summary>
    public async Task<int> IndexRepositoryAsync(
        string project,
        string repositoryId,
        string branch = "main")
    {
        _logger.LogInformation("Indexing repository {RepositoryId} on branch {Branch}", repositoryId, branch);

        // Get all files in repository
        var files = await _adoClient.GetRepositoryItemsAsync(
            project, repositoryId, branch);

        int indexed = 0;
        var chunks = new List<CodeChunk>();

        foreach (var filePath in files.Take(50)) // Limit to 50 files for now to control costs
        {
            // Skip binary files, tests, generated code
            if (ShouldSkipFile(filePath))
                continue;

            try
            {
                // Fetch file content
                var content = await _adoClient.GetFileContentAsync(
                    project, repositoryId, filePath, branch);

                if (string.IsNullOrEmpty(content) || content.Length < 50)
                    continue;

                // Split large files into chunks
                var fileChunks = SplitIntoChunks(content, filePath);

                foreach (var chunk in fileChunks)
                {
                    // Generate embedding for the chunk
                    var embedding = await _embeddingService.GenerateEmbeddingAsync(chunk.Content);
                    chunk.Embedding = embedding.ToArray();
                    chunks.Add(chunk);
                    indexed++;
                }

                _logger.LogDebug("Indexed {FilePath} ({ChunkCount} chunks)",
                    filePath, fileChunks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index {FilePath}", filePath);
            }
        }

        // Store all chunks in memory
        _inMemoryStore[repositoryId] = chunks;

        _logger.LogInformation("Indexed {Count} code chunks from repository {RepositoryId}",
            indexed, repositoryId);
        return indexed;
    }

    /// <summary>
    /// Get relevant context for a file being reviewed
    /// </summary>
    public async Task<string> GetRelevantContextAsync(
        PullRequestFile file,
        string repositoryId,
        int maxResults = 5)
    {
        var context = new StringBuilder();

        // Check if repository is indexed
        if (!_inMemoryStore.ContainsKey(repositoryId) || _inMemoryStore[repositoryId].Count == 0)
        {
            _logger.LogWarning("Repository {RepositoryId} is not indexed", repositoryId);
            return string.Empty;
        }

        // Build search query from file content and changes
        var searchQuery = BuildSearchQuery(file);

        if (string.IsNullOrEmpty(searchQuery))
        {
            return string.Empty;
        }

        try
        {
            // Generate embedding for the search query
            var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(searchQuery);
            var queryVector = queryEmbedding.ToArray();

            // Semantic search for similar code using cosine similarity
            var chunks = _inMemoryStore[repositoryId];
            var results = chunks
                .Select(chunk => new
                {
                    Chunk = chunk,
                    Similarity = CosineSimilarity(queryVector, chunk.Embedding)
                })
                .Where(r => r.Similarity > 0.7) // Relevance threshold
                .OrderByDescending(r => r.Similarity)
                .Take(maxResults)
                .ToList();

            if (!results.Any())
            {
                _logger.LogDebug("No relevant context found for {FilePath}", file.Path);
                return string.Empty;
            }

            context.AppendLine("## Relevant Codebase Context");
            context.AppendLine();

            foreach (var result in results)
            {
                context.AppendLine($"### Similar code (relevance: {result.Similarity:F2})");
                context.AppendLine($"Location: {result.Chunk.Metadata}");
                context.AppendLine("```");
                context.AppendLine(result.Chunk.Content.Length > 500
                    ? result.Chunk.Content.Substring(0, 500) + "..."
                    : result.Chunk.Content);
                context.AppendLine("```");
                context.AppendLine();
            }

            _logger.LogInformation("Found {Count} relevant code snippets for {FilePath}",
                results.Count, file.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching for relevant context");
        }

        return context.ToString();
    }

    /// <summary>
    /// Get dependency-based context (files that import/are imported by this file)
    /// </summary>
    public async Task<string> GetDependencyContextAsync(
        PullRequestFile file,
        string project,
        string repositoryId)
    {
        var context = new StringBuilder();

        // Parse imports/dependencies from file content
        var dependencies = ParseDependencies(file.Content, file.Path);

        if (!dependencies.Any())
        {
            return string.Empty;
        }

        context.AppendLine("## Related Files (Dependencies)");
        context.AppendLine();

        foreach (var dep in dependencies.Take(3)) // Limit to top 3
        {
            try
            {
                var depContent = await _adoClient.GetFileContentAsync(
                    project, repositoryId, dep, "main");

                if (string.IsNullOrEmpty(depContent))
                    continue;

                // Get summary (first 20 lines or class/interface definitions)
                var summary = GetFileSummary(depContent, 20);

                context.AppendLine($"### {dep}");
                context.AppendLine("```");
                context.AppendLine(summary);
                context.AppendLine("```");
                context.AppendLine();
            }
            catch
            {
                _logger.LogDebug("Could not fetch dependency {Dependency}", dep);
            }
        }

        return context.ToString();
    }

    /// <summary>
    /// Build comprehensive context for review
    /// </summary>
    public async Task<string> BuildReviewContextAsync(
        PullRequestFile file,
        PullRequest pr,
        string project,
        string repositoryId)
    {
        var context = new StringBuilder();

        // 1. PR-level context
        context.AppendLine($"# Pull Request Context");
        context.AppendLine($"**Title:** {pr.Title}");
        if (!string.IsNullOrEmpty(pr.Description))
        {
            context.AppendLine($"**Description:** {pr.Description}");
        }
        context.AppendLine();

        // 2. Semantic context (similar code)
        var semanticContext = await GetRelevantContextAsync(file, repositoryId, maxResults: 3);
        if (!string.IsNullOrEmpty(semanticContext))
        {
            context.AppendLine(semanticContext);
        }

        // 3. Dependency context (related files)
        var depContext = await GetDependencyContextAsync(file, project, repositoryId);
        if (!string.IsNullOrEmpty(depContext))
        {
            context.AppendLine(depContext);
        }

        return context.ToString();
    }

    // Helper methods

    private bool ShouldSkipFile(string path)
    {
        var skipPatterns = new[]
        {
            ".jpg", ".png", ".gif", ".pdf", ".zip", ".exe", ".dll",
            "node_modules/", "bin/", "obj/", ".git/", "packages/",
            "package-lock.json", "yarn.lock", "*.min.js", "*.min.css",
            ".generated.", "AssemblyInfo.cs"
        };

        return skipPatterns.Any(pattern =>
            path.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private List<CodeChunk> SplitIntoChunks(string content, string filePath)
    {
        var chunks = new List<CodeChunk>();
        var lines = content.Split('\n');
        const int CHUNK_SIZE = 100; // lines per chunk
        const int OVERLAP = 10; // overlap between chunks

        for (int i = 0; i < lines.Length; i += (CHUNK_SIZE - OVERLAP))
        {
            var chunkLines = lines.Skip(i).Take(CHUNK_SIZE).ToArray();
            if (chunkLines.Length == 0) break;

            chunks.Add(new CodeChunk
            {
                Content = string.Join('\n', chunkLines),
                ChunkIndex = chunks.Count,
                StartLine = i + 1,
                EndLine = i + chunkLines.Length,
                Metadata = $"{filePath}:L{i + 1}-L{i + chunkLines.Length}",
                FilePath = filePath,
                Embedding = Array.Empty<float>() // Will be filled during indexing
            });
        }

        return chunks;
    }

    private string BuildSearchQuery(PullRequestFile file)
    {
        var queryParts = new List<string>();

        // Extract meaningful content from changes
        if (!string.IsNullOrEmpty(file.UnifiedDiff))
        {
            var diffLines = file.UnifiedDiff.Split('\n')
                .Where(l => l.StartsWith('+') && !l.StartsWith("+++"))
                .Select(l => l.Substring(1).Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length > 5)
                .Take(10); // First 10 added lines

            queryParts.AddRange(diffLines);
        }

        // Add file name context
        var fileName = Path.GetFileNameWithoutExtension(file.Path);
        queryParts.Add($"file {fileName}");

        var query = string.Join(' ', queryParts);
        return query.Length > 1000 ? query.Substring(0, 1000) : query;
    }

    private List<string> ParseDependencies(string? content, string filePath)
    {
        if (string.IsNullOrEmpty(content))
            return new List<string>();

        var dependencies = new List<string>();
        var ext = Path.GetExtension(filePath);

        // Language-specific import parsing
        if (ext == ".cs")
        {
            // Parse: using Namespace.ClassName;
            var usingMatches = Regex.Matches(content, @"using\s+([A-Za-z0-9_.]+);");
            foreach (Match match in usingMatches)
            {
                var ns = match.Groups[1].Value;
                // Try to guess file path from namespace
                var potentialPath = "/" + ns.Replace(".", "/") + ".cs";
                dependencies.Add(potentialPath);
            }
        }
        else if (ext == ".py")
        {
            // Parse: from module import something / import module
            var importMatches = Regex.Matches(content, @"(?:from|import)\s+([A-Za-z0-9_.]+)");
            foreach (Match match in importMatches)
            {
                var module = match.Groups[1].Value;
                var potentialPath = "/" + module.Replace(".", "/") + ".py";
                dependencies.Add(potentialPath);
            }
        }
        else if (ext == ".rs")
        {
            // Parse: use crate::module::Type;
            var useMatches = Regex.Matches(content, @"use\s+(?:crate::)?([A-Za-z0-9_:]+)");
            foreach (Match match in useMatches)
            {
                var module = match.Groups[1].Value.Replace("::", "/");
                var potentialPath = "/src/" + module + ".rs";
                dependencies.Add(potentialPath);
            }
        }

        return dependencies.Distinct().Take(5).ToList();
    }

    private string GetFileSummary(string content, int maxLines)
    {
        var lines = content.Split('\n').Take(maxLines);
        return string.Join('\n', lines);
    }

    private double CosineSimilarity(float[] vectorA, float[] vectorB)
    {
        if (vectorA.Length != vectorB.Length)
            return 0;

        double dotProduct = 0;
        double magnitudeA = 0;
        double magnitudeB = 0;

        for (int i = 0; i < vectorA.Length; i++)
        {
            dotProduct += vectorA[i] * vectorB[i];
            magnitudeA += vectorA[i] * vectorA[i];
            magnitudeB += vectorB[i] * vectorB[i];
        }

        magnitudeA = Math.Sqrt(magnitudeA);
        magnitudeB = Math.Sqrt(magnitudeB);

        if (magnitudeA == 0 || magnitudeB == 0)
            return 0;

        return dotProduct / (magnitudeA * magnitudeB);
    }
}

/// <summary>
/// Represents a chunk of code with its embedding vector
/// </summary>
public class CodeChunk
{
    public string Content { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Metadata { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = Array.Empty<float>();
}
