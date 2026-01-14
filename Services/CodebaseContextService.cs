using System.Text;
using System.Text.RegularExpressions;
using CodeReviewAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;

namespace CodeReviewAgent.Services;

/// <summary>
/// Provides intelligent codebase context using RAG (Retrieval Augmented Generation)
/// </summary>
public class CodebaseContextService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly AzureDevOpsRestClient _adoClient;
    private readonly ILogger<CodebaseContextService> _logger;
    private readonly Dictionary<string, List<CodeChunk>> _inMemoryStore;
    private const string COLLECTION_NAME = "codebase";

    public CodebaseContextService(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        AzureDevOpsRestClient adoClient,
        ILogger<CodebaseContextService> logger)
    {
        _embeddingGenerator = embeddingGenerator;
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
        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║ RAG INDEXING: Starting Repository Indexing                ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
        _logger.LogInformation("Repository ID: {RepositoryId}", repositoryId);
        _logger.LogInformation("Project: {Project}", project);
        _logger.LogInformation("Branch: {Branch}", branch);

        // Get all files in repository
        _logger.LogInformation("Step 1: Fetching repository file tree...");
        var files = await _adoClient.GetRepositoryItemsAsync(
            project, repositoryId, branch);

        _logger.LogInformation("Found {FileCount} total files in repository", files.Count);

        if (files.Count == 0)
        {
            _logger.LogWarning("⚠️  No files found in repository. Indexing cannot proceed.");
            _logger.LogWarning("This could be due to:");
            _logger.LogWarning("  - Invalid branch name (currently: {Branch})", branch);
            _logger.LogWarning("  - Permissions issue with PAT");
            _logger.LogWarning("  - Repository is empty");
            return 0;
        }

        int indexed = 0;
        int skipped = 0;
        int failed = 0;
        var chunks = new List<CodeChunk>();

        _logger.LogInformation("Step 2: Processing files (limit: 50 files to control costs)...");

        foreach (var filePath in files.Take(50)) // Limit to 50 files for now to control costs
        {
            // Skip binary files, tests, generated code
            if (ShouldSkipFile(filePath))
            {
                skipped++;
                _logger.LogDebug("⏭️  Skipped (pattern match): {FilePath}", filePath);
                continue;
            }

            try
            {
                _logger.LogInformation("📄 Processing file: {FilePath}", filePath);

                // Fetch file content
                var content = await _adoClient.GetFileContentAsync(
                    project, repositoryId, filePath, branch);

                if (string.IsNullOrEmpty(content))
                {
                    skipped++;
                    _logger.LogDebug("⏭️  Skipped (empty file): {FilePath}", filePath);
                    continue;
                }

                if (content.Length < 50)
                {
                    skipped++;
                    _logger.LogDebug("⏭️  Skipped (too small, {Length} chars): {FilePath}",
                        content.Length, filePath);
                    continue;
                }

                _logger.LogInformation("  File size: {Size} bytes", content.Length);

                // Split large files into chunks
                var fileChunks = SplitIntoChunks(content, filePath);
                _logger.LogInformation("  Created {ChunkCount} chunks", fileChunks.Count);

                foreach (var chunk in fileChunks)
                {
                    _logger.LogDebug("  Generating embedding for chunk {Index} (lines {Start}-{End})...",
                        chunk.ChunkIndex, chunk.StartLine, chunk.EndLine);

                    // Generate embedding for the chunk
                    var embeddingResponse = await _embeddingGenerator.GenerateAsync(chunk.Content);
                    chunk.Embedding = embeddingResponse.Vector.ToArray();
                    chunks.Add(chunk);
                    indexed++;

                    _logger.LogDebug("  ✅ Embedding generated (dimension: {Dim})", chunk.Embedding.Length);
                }

                _logger.LogInformation("✅ Indexed {FilePath} ({ChunkCount} chunks)",
                    filePath, fileChunks.Count);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "❌ Failed to index {FilePath}: {Error}",
                    filePath, ex.Message);
            }
        }

        // Store all chunks in memory
        _inMemoryStore[repositoryId] = chunks;

        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║ RAG INDEXING: Completed                                    ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
        _logger.LogInformation("📊 Indexing Statistics:");
        _logger.LogInformation("  Total files found: {Total}", files.Count);
        _logger.LogInformation("  Files processed: {Processed}", indexed > 0 ? indexed / chunks.Count * files.Count : 0);
        _logger.LogInformation("  Files skipped: {Skipped}", skipped);
        _logger.LogInformation("  Files failed: {Failed}", failed);
        _logger.LogInformation("  Code chunks created: {Chunks}", chunks.Count);
        _logger.LogInformation("  Embeddings generated: {Embeddings}", indexed);
        _logger.LogInformation("  Vector dimension: {Dim}", chunks.Any() ? chunks[0].Embedding.Length : 0);
        _logger.LogInformation("════════════════════════════════════════════════════════════");

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
            _logger.LogInformation("Search query is empty for {FilePath}", file.Path);
            return string.Empty;
        }

        _logger.LogInformation("Search Query ({Length} chars): {Query}",
            searchQuery.Length, searchQuery.Length > 200 ? searchQuery.Substring(0, 200) + "..." : searchQuery);

        try
        {
            // Generate embedding for the search query
            _logger.LogInformation("Generating embedding for search query...");
            var queryEmbeddingResponse = await _embeddingGenerator.GenerateAsync(searchQuery);
            var queryVector = queryEmbeddingResponse.Vector.ToArray();
            _logger.LogInformation("Query embedding generated (dimension: {Dim})", queryVector.Length);

            // Semantic search for similar code using cosine similarity
            var chunks = _inMemoryStore[repositoryId];
            _logger.LogInformation("Searching through {Count} code chunks...", chunks.Count);

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
                _logger.LogInformation("No relevant context found for {FilePath} (threshold: 0.7)", file.Path);
                return string.Empty;
            }

            _logger.LogInformation("Found {Count} relevant snippets:", results.Count);
            foreach (var result in results)
            {
                _logger.LogInformation("  - {Location} (similarity: {Similarity:F3})",
                    result.Chunk.Metadata, result.Similarity);
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
            _logger.LogInformation("No dependencies found in {FilePath}", file.Path);
            return string.Empty;
        }

        _logger.LogInformation("Found {Count} dependencies in {FilePath}:", dependencies.Count, file.Path);
        foreach (var dep in dependencies)
        {
            _logger.LogInformation("  - {Dependency}", dep);
        }

        context.AppendLine("## Related Files (Dependencies)");
        context.AppendLine();

        int fetchedCount = 0;
        foreach (var dep in dependencies.Take(3)) // Limit to top 3
        {
            try
            {
                _logger.LogInformation("Fetching dependency file: {Dependency}", dep);
                var depContent = await _adoClient.GetFileContentAsync(
                    project, repositoryId, dep, "main");

                if (string.IsNullOrEmpty(depContent))
                {
                    _logger.LogInformation("Dependency file {Dependency} is empty", dep);
                    continue;
                }

                // Get summary (first 20 lines or class/interface definitions)
                var summary = GetFileSummary(depContent, 20);

                context.AppendLine($"### {dep}");
                context.AppendLine("```");
                context.AppendLine(summary);
                context.AppendLine("```");
                context.AppendLine();

                fetchedCount++;
                _logger.LogInformation("Successfully fetched dependency: {Dependency} ({Length} chars)",
                    dep, summary.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fetch dependency {Dependency}", dep);
            }
        }

        _logger.LogInformation("Successfully fetched {Count} out of {Total} dependencies",
            fetchedCount, Math.Min(3, dependencies.Count));

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

        _logger.LogInformation("========================================");
        _logger.LogInformation("Building RAG context for file: {FilePath}", file.Path);
        _logger.LogInformation("========================================");

        // 1. PR-level context
        context.AppendLine($"# Pull Request Context");
        context.AppendLine($"**Title:** {pr.Title}");
        if (!string.IsNullOrEmpty(pr.Description))
        {
            context.AppendLine($"**Description:** {pr.Description}");
        }
        context.AppendLine();

        _logger.LogInformation("PR Context: Title='{Title}', Description='{Description}'",
            pr.Title, pr.Description ?? "(none)");

        // 2. Semantic context (similar code)
        _logger.LogInformation("Searching for semantically similar code...");
        var semanticContext = await GetRelevantContextAsync(file, repositoryId, maxResults: 3);
        if (!string.IsNullOrEmpty(semanticContext))
        {
            context.AppendLine(semanticContext);
            _logger.LogInformation("Semantic Context Added ({Length} chars):\n{Context}",
                semanticContext.Length, semanticContext);
        }
        else
        {
            _logger.LogInformation("No semantic context found");
        }

        // 3. Dependency context (related files)
        _logger.LogInformation("Searching for dependency context...");
        var depContext = await GetDependencyContextAsync(file, project, repositoryId);
        if (!string.IsNullOrEmpty(depContext))
        {
            context.AppendLine(depContext);
            _logger.LogInformation("Dependency Context Added ({Length} chars):\n{Context}",
                depContext.Length, depContext);
        }
        else
        {
            _logger.LogInformation("No dependency context found");
        }

        var finalContext = context.ToString();
        _logger.LogInformation("========================================");
        _logger.LogInformation("FINAL RAG CONTEXT ({Length} chars total):", finalContext.Length);
        _logger.LogInformation("{Context}", finalContext);
        _logger.LogInformation("========================================");

        return finalContext;
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
