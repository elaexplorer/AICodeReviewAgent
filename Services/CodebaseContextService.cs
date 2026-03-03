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
    private const int CloneTimeoutSeconds = 60;

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
    /// Check if a repository has been indexed
    /// </summary>
    public bool IsRepositoryIndexed(string repositoryId)
    {
        return _inMemoryStore.ContainsKey(repositoryId) && _inMemoryStore[repositoryId].Count > 0;
    }

    /// <summary>
    /// Get the number of chunks indexed for a repository
    /// </summary>
    public int GetChunkCount(string repositoryId)
    {
        return _inMemoryStore.TryGetValue(repositoryId, out var chunks) ? chunks.Count : 0;
    }

    /// <summary>
    /// Get a summary of what's indexed (for debugging/logging)
    /// </summary>
    public string GetIndexSummary(string repositoryId)
    {
        if (!_inMemoryStore.TryGetValue(repositoryId, out var chunks) || chunks.Count == 0)
        {
            return $"Repository '{repositoryId}' is not indexed.";
        }

        var files = chunks.Select(c => c.FilePath).Distinct().ToList();
        var summary = new StringBuilder();
        summary.AppendLine($"Repository '{repositoryId}' Index Summary:");
        summary.AppendLine($"  Total chunks: {chunks.Count}");
        summary.AppendLine($"  Total files: {files.Count}");
        summary.AppendLine($"  Vector dimension: {(chunks.FirstOrDefault()?.Embedding.Length ?? 0)}");
        summary.AppendLine($"  Files indexed:");
        foreach (var file in files.Take(20))
        {
            var fileChunks = chunks.Count(c => c.FilePath == file);
            summary.AppendLine($"    - {file} ({fileChunks} chunks)");
        }
        if (files.Count > 20)
        {
            summary.AppendLine($"    ... and {files.Count - 20} more files");
        }
        return summary.ToString();
    }

    /// <summary>
    /// Index the entire repository using git clone for comprehensive file discovery
    /// This method clones the repository locally and indexes all files
    /// </summary>
    public async Task<int> IndexRepositoryWithCloneAsync(
        string project,
        string repositoryId,
        string branch = "master",
        string? repositoryUrl = null)
    {
        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║ RAG INDEXING: Starting Git Clone-Based Repository Indexing ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
        _logger.LogInformation("Repository ID: {RepositoryId}", repositoryId);
        _logger.LogInformation("Project: {Project}", project);
        _logger.LogInformation("Branch: {Branch}", branch);

        // Step 1: Prepare clone directory (in current codebase for easy access)
        var currentDir = Directory.GetCurrentDirectory();
        var tempReposDir = Path.Combine(currentDir, "temp_repos");
        var tempDir = Path.Combine(tempReposDir, repositoryId);
        var cloneDir = Path.Combine(tempDir, "repo");
        
        try
        {
            _logger.LogInformation("Step 1: Preparing clone directory: {CloneDir}", cloneDir);
            
            // Check if directory already exists and is valid
            if (Directory.Exists(cloneDir))
            {
                _logger.LogInformation("   📁 Clone directory already exists: {CloneDir}", cloneDir);
                _logger.LogInformation("   🔍 Checking if existing clone is valid...");
                
                // Check if it's a valid git repo with files
                var gitDir = Path.Combine(cloneDir, ".git");
                if (Directory.Exists(gitDir))
                {
                    var fileCount = Directory.GetFiles(cloneDir, "*.*", SearchOption.AllDirectories)
                        .Where(f => !IsGitFile(f))
                        .Count();
                    
                    if (fileCount > 0)
                    {
                        _logger.LogInformation("   ✅ Existing clone is valid with {FileCount} files, reusing it", fileCount);
                        _logger.LogInformation("   ⚡ Skipping git clone - using existing repository");
                        goto ProcessFiles; // Skip cloning, go directly to file processing
                    }
                }
                
                _logger.LogInformation("   🧹 Existing clone is invalid, cleaning up...");
                await ForceDeleteDirectoryAsync(tempDir);
            }
            Directory.CreateDirectory(tempDir);

            // Step 2: Construct repository URL and clone
            var repoUrl = repositoryUrl ?? $"https://dev.azure.com/{_adoClient.Organization}/{project}/_git/{repositoryId}";
            repoUrl = BuildAuthenticatedCloneUrl(repoUrl);
            var adoPat = _adoClient.PersonalAccessToken;
            var logUrl = !string.IsNullOrEmpty(adoPat) ? repoUrl.Replace(adoPat, "***") : repoUrl;
            _logger.LogInformation("Step 2: Cloning repository from: {RepoUrl}", logUrl);
            _logger.LogInformation("   Requested branch: {Branch} (will auto-detect if this fails)", branch);

            // Try cloning the specified branch first, fallback to default branch if it fails
            _logger.LogInformation("🔧 Attempting to clone branch '{Branch}' (shallow clone only)...", branch);
            _logger.LogInformation("   Using --depth 1 --single-branch for optimized shallow clone");
            _logger.LogInformation("   Using {TimeoutSeconds}-second timeout for shallow clone", CloneTimeoutSeconds);
            var cloneResult = await RunGitCommandAsync($"clone --depth 1 --single-branch --branch {branch} \"{repoUrl}\" \"{cloneDir}\"", tempDir, CloneTimeoutSeconds);
            
            if (!cloneResult.Success)
            {
                _logger.LogWarning("⚠️ Failed to clone branch '{Branch}': {Error}", branch, cloneResult.Error);
                
                // Clean up any partial/empty directories left by failed clone
                if (Directory.Exists(cloneDir))
                {
                    _logger.LogInformation("   🧹 Cleaning up partial clone directory from failed attempt...");
                    await CleanupCloneDirectoryAsync(tempDir);
                    Directory.CreateDirectory(tempDir);
                }
                
                _logger.LogInformation("🔧 Attempting to clone default branch (no branch specified)...");
                
                // Try cloning without specifying branch (uses default branch)
                cloneResult = await RunGitCommandAsync($"clone --depth 1 --single-branch \"{repoUrl}\" \"{cloneDir}\"", tempDir, CloneTimeoutSeconds);
            }
            
            if (!cloneResult.Success)
            {
                _logger.LogError("❌ Git clone failed: {Error}", cloneResult.Error);
                return 0;
            }

            _logger.LogInformation("✅ Repository cloned successfully");

            ProcessFiles:
            // Step 3: Discover all files using file system
            _logger.LogInformation("Step 3: Discovering files using file system traversal...");
            var allFiles = Directory.GetFiles(cloneDir, "*.*", SearchOption.AllDirectories)
                .Where(f => !IsGitFile(f)) // Skip .git directory files
                .Select(f => Path.GetRelativePath(cloneDir, f).Replace('\\', '/')) // Normalize to forward slashes
                .ToList();

            _logger.LogInformation("Found {FileCount} total files via file system (vs API discovery)", allFiles.Count);

            // Step 4: Process files with actual content from disk
            var chunks = await ProcessFilesFromDiskAsync(cloneDir, allFiles);

            // Step 5: Store in memory index
            _inMemoryStore[repositoryId] = chunks;
            
            _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║ RAG INDEXING: Git Clone-Based Indexing Complete            ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("📊 FINAL SUMMARY:");
            _logger.LogInformation("   Files discovered: {FileCount}", allFiles.Count);
            _logger.LogInformation("   Chunks created: {ChunkCount}", chunks.Count);
            _logger.LogInformation("   Files indexed: {IndexedCount}", chunks.Select(c => c.FilePath).Distinct().Count());
            _logger.LogInformation("════════════════════════════════════════════════════════════");

            return chunks.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Git clone-based indexing failed");
            return 0;
        }
        finally
        {
            // Cleanup
            try
            {
                if (Directory.Exists(tempDir))
                {
                    _logger.LogInformation("🧹 Cleaning up clone directory...");
                    await CleanupCloneDirectoryAsync(tempDir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️  Failed to cleanup clone directory: {TempDir}", tempDir);
            }
        }
    }

    /// <summary>
    /// Index the entire repository for semantic search using Git Clone only
    /// Run this once when PR is opened, or periodically
    /// </summary>
    public async Task<int> IndexRepositoryAsync(
        string project,
        string repositoryId,
        string branch = "master")
    {
        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║ RAG INDEXING: Starting Repository Indexing (Git Clone Only) ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
        _logger.LogInformation("Repository ID: {RepositoryId}", repositoryId);
        _logger.LogInformation("Project: {Project}", project);
        _logger.LogInformation("Branch: {Branch}", branch);

        // Use Git Clone approach only - no API fallback
        _logger.LogInformation("🚀 Using Git Clone approach for comprehensive repository indexing...");
        _logger.LogInformation("   This ensures we get ALL files from the repository");
        _logger.LogInformation("   Using shallow clone to avoid large binary files and improve performance");
        
        try
        {
            var cloneResult = await IndexRepositoryWithCloneAsync(project, repositoryId, branch);
            if (cloneResult > 0)
            {
                _logger.LogInformation("✅ Git Clone approach succeeded: {ChunkCount} chunks indexed", cloneResult);
                return cloneResult;
            }
            else
            {
                _logger.LogWarning("⚠️ Git Clone approach returned 0 chunks - attempting API fallback indexing...");

                var (apiSuccess, apiIndexedCount) = await TryIndexWithApiAsync(project, repositoryId, branch);
                if (apiSuccess && apiIndexedCount > 0)
                {
                    _logger.LogInformation("✅ API fallback succeeded: {ChunkCount} chunks indexed", apiIndexedCount);
                    return apiIndexedCount;
                }

                _logger.LogError("❌ Both Git Clone and API fallback indexing failed");
                return 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Git Clone approach failed - attempting API fallback...");

            var (apiSuccess, apiIndexedCount) = await TryIndexWithApiAsync(project, repositoryId, branch);
            if (apiSuccess && apiIndexedCount > 0)
            {
                _logger.LogInformation("✅ API fallback succeeded after clone exception: {ChunkCount} chunks indexed", apiIndexedCount);
                return apiIndexedCount;
            }

            _logger.LogError("❌ API fallback also failed after clone exception");
            return 0;
        }
    }

    /// <summary>
    /// Try indexing using Azure DevOps REST API
    /// </summary>
    private async Task<(bool Success, int IndexedCount)> TryIndexWithApiAsync(
        string project,
        string repositoryId, 
        string branch)
    {
        try
        {
            // Use the specified branch directly - no need to discover all branches
            _logger.LogInformation("API Method: Using specified branch '{Branch}' directly", branch);
            
            // Get all files from the specified branch
            _logger.LogInformation("API Method: Fetching repository file tree from branch '{Branch}'...", branch);
            var files = await _adoClient.GetRepositoryItemsAsync(project, repositoryId, branch);

            _logger.LogInformation("Found {FileCount} total files in repository", files.Count);

            if (files.Count == 0)
            {
                _logger.LogWarning("⚠️  No files found in repository using API method");
                return (false, 0);
            }

            int indexed = 0;
            int skipped = 0;
            int failed = 0;
            var chunks = new List<CodeChunk>();

            _logger.LogInformation("API Method: Processing {FileCount} files...", files.Count);

            // Process first 20 files to test API reliability
            var apiTestFiles = files.Take(20).ToList();
            int apiErrors = 0;
            
            _logger.LogInformation("📄 BATCH 1/2: Processing first 20 files for API validation...");
            
            foreach (var filePath in apiTestFiles)
            {
                // Skip binary files, tests, generated code
                if (ShouldSkipFile(filePath))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    _logger.LogInformation("📄 Processing file {FileIndex}/{TotalFiles}: {FilePath}", 
                        apiTestFiles.IndexOf(filePath) + 1, apiTestFiles.Count, filePath);
                    
                    // Fetch file content
                    var content = await _adoClient.GetFileContentAsync(
                        project, repositoryId, filePath, branch);

                    if (string.IsNullOrEmpty(content))
                    {
                        apiErrors++;
                        continue;
                    }

                    if (content.Length < 50)
                    {
                        skipped++;
                        continue;
                    }

                    // Split large files into chunks
                    var fileChunks = SplitIntoChunks(content, filePath);
                    
                    // Generate embeddings for each chunk
                    _logger.LogInformation("   🧩 Generating embeddings for {ChunkCount} chunks...", fileChunks.Count);
                    foreach (var chunk in fileChunks)
                    {
                        var embeddingResponse = await _embeddingGenerator.GenerateAsync(chunk.Content);
                        chunk.Embedding = embeddingResponse.Vector.ToArray();
                        chunks.Add(chunk);
                        indexed++;
                        
                        if (indexed % 10 == 0) // Log every 10 chunks
                        {
                            _logger.LogInformation("   ✅ {IndexedCount} chunks embedded so far...", indexed);
                        }
                    }
                }
                catch (Exception ex)
                {
                    apiErrors++;
                    failed++;
                    _logger.LogDebug("API error for {FilePath}: {Error}", filePath, ex.Message);
                }
            }
            
            // If too many API errors (>50% of test files), consider API approach failed
            if (apiErrors > apiTestFiles.Count * 0.5)
            {
                _logger.LogWarning("⚠️  API approach has too many errors ({Errors}/{Total} files failed)", 
                    apiErrors, apiTestFiles.Count);
                return (false, 0);
            }
            
            // If API is working, continue with all files
            _logger.LogInformation("✅ API approach is working, continuing with all {FileCount} files", files.Count);
            
            var remainingFiles = files.Skip(20).ToList();
            _logger.LogInformation("📄 BATCH 2/2: Processing remaining {FileCount} files...", remainingFiles.Count);
            
            foreach (var filePath in remainingFiles) // Process remaining files
            {
                // Skip binary files, tests, generated code
                if (ShouldSkipFile(filePath))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var currentIndex = remainingFiles.IndexOf(filePath) + 1;
                    var totalRemaining = remainingFiles.Count;
                    var overallIndex = currentIndex + 20; // Add the 20 from first batch
                    var overallTotal = files.Count;
                    
                    _logger.LogInformation("📄 Processing file {CurrentIndex}/{TotalRemaining} (overall {OverallIndex}/{OverallTotal}): {FilePath}", 
                        currentIndex, totalRemaining, overallIndex, overallTotal, filePath);
                    
                    // Fetch file content
                    var content = await _adoClient.GetFileContentAsync(
                        project, repositoryId, filePath, branch);

                    if (string.IsNullOrEmpty(content))
                    {
                        skipped++;
                        continue;
                    }

                    if (content.Length < 50)
                    {
                        skipped++;
                        continue;
                    }

                    // Split large files into chunks
                    var fileChunks = SplitIntoChunks(content, filePath);
                    
                    // Generate embeddings for each chunk
                    _logger.LogInformation("   🧩 Generating embeddings for {ChunkCount} chunks...", fileChunks.Count);
                    foreach (var chunk in fileChunks)
                    {
                        try
                        {
                            var embedding = await GenerateEmbeddingWithRetryAsync(chunk.Content);
                            chunk.Embedding = ((dynamic)embedding).Vector.ToArray();
                            chunks.Add(chunk);
                            indexed++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            _logger.LogError("❌ Failed to generate embedding for chunk {Index} from {FilePath}: {Error}", 
                                chunk.ChunkIndex, filePath, ex.Message);
                            _logger.LogInformation("⏭️  Skipping problematic chunk and continuing...");
                        }
                        
                        if ((indexed + failed) % 10 == 0) // Log every 10 chunks
                        {
                            _logger.LogInformation("   ✅ {IndexedCount} chunks embedded so far...", indexed);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogDebug("API error for {FilePath}: {Error}", filePath, ex.Message);
                }
            }
            
            // Store all chunks in memory
            _inMemoryStore[repositoryId] = chunks;

            _logger.LogInformation("✅ API Method: Indexed {Count} chunks from {Files} files", indexed, files.Count);
            return (true, indexed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ API indexing method failed");
            return (false, 0);
        }
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

        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║ RAG RETRIEVAL: Starting Context Search                     ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
        _logger.LogInformation("Target file: {FilePath}", file.Path);

        // Check if repository is indexed
        if (!_inMemoryStore.ContainsKey(repositoryId) || _inMemoryStore[repositoryId].Count == 0)
        {
            _logger.LogWarning("⚠️  Repository {RepositoryId} is NOT indexed!", repositoryId);
            _logger.LogWarning("   Call IndexRepositoryAsync() first to index the codebase.");
            return string.Empty;
        }

        _logger.LogInformation("✅ Repository is indexed. Storage key: '{RepositoryId}'", repositoryId);
        _logger.LogInformation("   Available chunks: {Count}", _inMemoryStore[repositoryId].Count);

        // Build search query from file content and changes
        _logger.LogInformation("Step 1: Building search query from PR diff...");
        var searchQuery = BuildSearchQuery(file);

        if (string.IsNullOrEmpty(searchQuery))
        {
            _logger.LogInformation("⚠️  Search query is empty for {FilePath} - no added lines in diff", file.Path);
            return string.Empty;
        }

        _logger.LogInformation("🔍 SEARCH QUERY CONSTRUCTED:");
        _logger.LogInformation("   Length: {Length} characters", searchQuery.Length);
        _logger.LogInformation("   Content: {Query}",
            searchQuery.Length > 300 ? searchQuery.Substring(0, 300) + "..." : searchQuery);

        try
        {
            // Generate embedding for the search query
            _logger.LogInformation("Step 2: Generating embedding for search query...");
            var queryEmbeddingResponse = await GenerateEmbeddingWithRetryAsync(searchQuery);
            var queryVector = ToFloatVector(((dynamic)queryEmbeddingResponse).Vector);

            _logger.LogInformation("📐 QUERY EMBEDDING GENERATED:");
            _logger.LogInformation("   Vector dimension: {Dim}", (int)queryVector.Length);
            _logger.LogInformation("   First 5 values: [{Values}]",
                string.Join(", ", ((float[])queryVector).Take(5).Select(v => v.ToString("F6"))));
            _logger.LogInformation("   Vector range: [{Min:F6} to {Max:F6}]", ((float[])queryVector).Min(), ((float[])queryVector).Max());

            // Semantic search for similar code using cosine similarity
            var chunks = _inMemoryStore[repositoryId];
            _logger.LogInformation("Step 3: Calculating cosine similarity against {Count} chunks...", chunks.Count);

            // Calculate all similarities for logging
            var allSimilarities = chunks
                .Select(chunk => new SimilarityResult
                {
                    Chunk = chunk,
                    Similarity = CosineSimilarity(queryVector, chunk.Embedding)
                })
                .OrderByDescending(r => r.Similarity)
                .ToList();

            // Log similarity distribution
            _logger.LogInformation("📊 SIMILARITY DISTRIBUTION:");
            _logger.LogInformation("   Max similarity: {Max:F4}", (double)allSimilarities.First().Similarity);
            _logger.LogInformation("   Min similarity: {Min:F4}", (double)allSimilarities.Last().Similarity);
            _logger.LogInformation("   Mean similarity: {Mean:F4}", allSimilarities.Average(s => s.Similarity));

            var aboveThreshold = allSimilarities.Count(s => s.Similarity > 0.4);
            var above03 = allSimilarities.Count(s => s.Similarity > 0.3 && s.Similarity <= 0.4);
            var above02 = allSimilarities.Count(s => s.Similarity > 0.2 && s.Similarity <= 0.3);
            var below02 = allSimilarities.Count(s => s.Similarity <= 0.2);

            _logger.LogInformation("   Chunks with similarity > 0.4 (threshold): {Count}", aboveThreshold);
            _logger.LogInformation("   Chunks with similarity 0.3-0.4: {Count}", above03);
            _logger.LogInformation("   Chunks with similarity 0.2-0.3: {Count}", above02);
            _logger.LogInformation("   Chunks with similarity <= 0.2: {Count}", below02);

            // Top 10 matches regardless of threshold (for debugging)
            _logger.LogInformation("🔝 TOP 10 MATCHES (before threshold filter):");
            foreach (var match in allSimilarities.Take(10))
            {
                _logger.LogInformation("   {Similarity:F4} - {Location}",
                    (double)match.Similarity, (string)match.Chunk.Metadata);
            }

            var results = allSimilarities
                .Where(r => r.Similarity > 0.4) // Lowered threshold for better context retrieval
                .Take(maxResults)
                .ToList();

            if (results.Count == 0)
            {
                var best = allSimilarities.FirstOrDefault();
                if (best != null && best.Similarity >= 0.25)
                {
                    _logger.LogInformation("⚠️ No chunks passed the 0.4 threshold for {FilePath}; using best fallback match {Similarity:F4}",
                        file.Path, (double)best.Similarity);
                    results = new List<SimilarityResult> { best };
                }
                else
                {
                    _logger.LogInformation("❌ No chunks passed the 0.4 threshold and fallback did not qualify for {FilePath}", file.Path);
                    if (best != null)
                    {
                        _logger.LogInformation("   Closest match was: {Similarity:F4} at {Location}",
                            (double)best.Similarity, (string)best.Chunk.Metadata);
                    }

                    // Hybrid fallback: enrich query with full-file/package/path signals.
                    _logger.LogInformation("Step 3b: Retrying semantic search with hybrid full-file query...");
                    var hybridQuery = BuildHybridSearchQuery(file, searchQuery);

                    if (!string.IsNullOrWhiteSpace(hybridQuery))
                    {
                        _logger.LogInformation("🔍 HYBRID QUERY CONSTRUCTED:");
                        _logger.LogInformation("   Length: {Length} characters", hybridQuery.Length);
                        _logger.LogInformation("   Content: {Query}",
                            hybridQuery.Length > 300 ? hybridQuery.Substring(0, 300) + "..." : hybridQuery);

                        var hybridEmbeddingResponse = await GenerateEmbeddingWithRetryAsync(hybridQuery);
                        var hybridVector = ToFloatVector(((dynamic)hybridEmbeddingResponse).Vector);

                        var hybridSimilarities = chunks
                            .Select(chunk => new SimilarityResult
                            {
                                Chunk = chunk,
                                Similarity = CosineSimilarity(hybridVector, chunk.Embedding)
                            })
                            .OrderByDescending(r => r.Similarity)
                            .ToList();

                        var hybridBest = hybridSimilarities.FirstOrDefault();
                        _logger.LogInformation("   Hybrid max similarity: {Max:F4}", (double)(hybridBest?.Similarity ?? 0));

                        results = hybridSimilarities
                            .Where(r => r.Similarity > 0.3)
                            .Take(maxResults)
                            .ToList();

                        if (results.Count == 0 && hybridBest != null && hybridBest.Similarity >= 0.18)
                        {
                            _logger.LogInformation("⚠️ Hybrid semantic fallback selected best match {Similarity:F4}", (double)hybridBest.Similarity);
                            results = new List<SimilarityResult> { hybridBest };
                            queryVector = hybridVector;
                        }
                        else if (results.Count > 0)
                        {
                            _logger.LogInformation("✅ Hybrid semantic search found {Count} chunks", results.Count);
                            queryVector = hybridVector;
                        }
                    }

                    // Deterministic fallback: choose chunks from same package/path neighborhood.
                    if (results.Count == 0)
                    {
                        _logger.LogInformation("Step 3c: Semantic retrieval still empty; using path/package fallback...");
                        results = GetPathBasedFallbackChunks(file, chunks, maxResults);
                        if (results.Count > 0)
                        {
                            _logger.LogInformation("✅ Path/package fallback provided {Count} chunks", results.Count);
                        }
                    }

                    if (results.Count == 0)
                    {
                        _logger.LogInformation("❌ No semantic or fallback context found for {FilePath}", file.Path);
                        return string.Empty;
                    }
                }
            }

            _logger.LogInformation("✅ RETRIEVAL RESULTS: Found {Count} relevant chunks above threshold", results.Count);
            _logger.LogInformation("Step 4: Building context from retrieved chunks...");

            context.AppendLine("## Relevant Codebase Context");
            context.AppendLine();

            int resultIndex = 1;
            foreach (var result in results)
            {
                _logger.LogInformation("📄 RETRIEVED CHUNK {Index} - DETAILED ANALYSIS:", resultIndex);
                _logger.LogInformation("   File: {FilePath}", result.Chunk.FilePath);
                _logger.LogInformation("   Lines: {Start} to {End}", result.Chunk.StartLine, result.Chunk.EndLine);
                _logger.LogInformation("   Similarity Score: {Similarity:F4}", (double)result.Similarity);
                
                _logger.LogInformation("   🔤 CHUNK CONTENT ({Length} chars):", result.Chunk.Content.Length);
                _logger.LogInformation("   Full content: {Content}", result.Chunk.Content.Replace("\n", "\\n"));
                
                _logger.LogInformation("   📊 VECTOR DETAILS:");
                _logger.LogInformation("     Chunk vector first 5: [{Values}]", 
                    string.Join(", ", result.Chunk.Embedding.Take(5).Select(v => v.ToString("F6"))));
                _logger.LogInformation("     Chunk vector last 5: [{Values}]", 
                    string.Join(", ", result.Chunk.Embedding.TakeLast(5).Select(v => v.ToString("F6"))));
                _logger.LogInformation("     Chunk vector range: [{Min:F6}, {Max:F6}]", 
                    result.Chunk.Embedding.Min(), result.Chunk.Embedding.Max());
                
                _logger.LogInformation("   🧮 SIMILARITY CALCULATION:");
                var dotProduct = 0.0;
                var queryMagnitude = 0.0;
                var chunkMagnitude = 0.0;
                
                for (int i = 0; i < Math.Min(queryVector.Length, result.Chunk.Embedding.Length); i++)
                {
                    dotProduct += queryVector[i] * result.Chunk.Embedding[i];
                    queryMagnitude += queryVector[i] * queryVector[i];
                    chunkMagnitude += result.Chunk.Embedding[i] * result.Chunk.Embedding[i];
                }
                queryMagnitude = Math.Sqrt(queryMagnitude);
                chunkMagnitude = Math.Sqrt(chunkMagnitude);
                
                _logger.LogInformation("     Dot product: {DotProduct:F6}", dotProduct);
                _logger.LogInformation("     Query magnitude: {QueryMag:F6}", queryMagnitude);
                _logger.LogInformation("     Chunk magnitude: {ChunkMag:F6}", chunkMagnitude);
                _logger.LogInformation("     Cosine similarity: {Cosine:F6}", dotProduct / (queryMagnitude * chunkMagnitude));

                context.AppendLine($"### Similar code (relevance: {result.Similarity:F2})");
                context.AppendLine($"Location: {result.Chunk.Metadata}");
                context.AppendLine("```");
                context.AppendLine(result.Chunk.Content.Length > 500
                    ? result.Chunk.Content.Substring(0, 500) + "..."
                    : result.Chunk.Content);
                context.AppendLine("```");
                context.AppendLine();
                resultIndex++;
            }

            _logger.LogInformation("════════════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error searching for relevant context");
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
        var overallStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var context = new StringBuilder();

        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║ RAG CONTEXT BUILDING: Starting for {File}", file.Path.Length > 35 ? "..." + file.Path.Substring(file.Path.Length - 32) : file.Path);
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");

        // Keep per-file RAG focused on the target file and related code.
        context.AppendLine("## File-Specific RAG Context");
        context.AppendLine($"Target file: {file.Path}");
        context.AppendLine();

        _logger.LogInformation("File-focused RAG context for '{FilePath}' (PR title: {Title})",
            file.Path, pr.Title);

        // 2. Semantic context (similar code)
        _logger.LogInformation("Step 1: Searching for semantically similar code...");
        var semanticStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var semanticContext = await GetRelevantContextAsync(file, repositoryId, maxResults: 3);
        semanticStopwatch.Stop();
        
        if (!string.IsNullOrEmpty(semanticContext))
        {
            context.AppendLine(semanticContext);
            _logger.LogInformation("✅ Semantic Context Added in {ElapsedMs}ms:", semanticStopwatch.ElapsedMilliseconds);
            _logger.LogInformation("   Length: {Length} chars", semanticContext.Length);
            _logger.LogInformation("   Content Preview:\n{Preview}", 
                semanticContext.Length > 500 ? semanticContext.Substring(0, 500) + "\n... [TRUNCATED]" : semanticContext);
        }
        else
        {
            _logger.LogInformation("❌ No semantic context found ({ElapsedMs}ms)", semanticStopwatch.ElapsedMilliseconds);
        }

        // 3. Dependency context (related files)
        _logger.LogInformation("Step 2: Searching for dependency context...");
        var depStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var depContext = await GetDependencyContextAsync(file, project, repositoryId);
        depStopwatch.Stop();
        
        if (!string.IsNullOrEmpty(depContext))
        {
            context.AppendLine(depContext);
            _logger.LogInformation("✅ Dependency Context Added in {ElapsedMs}ms:", depStopwatch.ElapsedMilliseconds);
            _logger.LogInformation("   Length: {Length} chars", depContext.Length);
            _logger.LogInformation("   Content Preview:\n{Preview}", 
                depContext.Length > 500 ? depContext.Substring(0, 500) + "\n... [TRUNCATED]" : depContext);
        }
        else
        {
            _logger.LogInformation("❌ No dependency context found ({ElapsedMs}ms)", depStopwatch.ElapsedMilliseconds);
        }

        // 4. Test context (test files likely covering the target file)
        _logger.LogInformation("Step 3: Searching for related test context...");
        var testStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var testContext = GetRelatedTestContext(file, repositoryId, maxFiles: 2, maxChunksPerFile: 2);
        testStopwatch.Stop();

        if (!string.IsNullOrEmpty(testContext))
        {
            context.AppendLine(testContext);
            _logger.LogInformation("✅ Test Context Added in {ElapsedMs}ms:", testStopwatch.ElapsedMilliseconds);
            _logger.LogInformation("   Length: {Length} chars", testContext.Length);
        }
        else
        {
            _logger.LogInformation("❌ No related test context found ({ElapsedMs}ms)", testStopwatch.ElapsedMilliseconds);
        }

        overallStopwatch.Stop();
        var finalContext = context.ToString();
        
        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║ FINAL RAG CONTEXT SENT TO LLM                             ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
        _logger.LogInformation("⏱️  RAG TIMING BREAKDOWN:");
        _logger.LogInformation("   Semantic search: {SemanticMs}ms", semanticStopwatch?.ElapsedMilliseconds ?? 0);
        _logger.LogInformation("   Dependency search: {DepMs}ms", depStopwatch?.ElapsedMilliseconds ?? 0);
        _logger.LogInformation("   Test search: {TestMs}ms", testStopwatch?.ElapsedMilliseconds ?? 0);
        _logger.LogInformation("   Total RAG time: {TotalMs}ms ({TotalSec:F2} seconds)", 
            overallStopwatch.ElapsedMilliseconds, overallStopwatch.Elapsed.TotalSeconds);
        
        _logger.LogInformation("📏 Context Statistics:");
        _logger.LogInformation("   Total length: {Length} characters", finalContext.Length);
        _logger.LogInformation("   Line count: {Lines}", finalContext.Split('\n').Length);
        _logger.LogInformation("   Word count: ~{Words}", finalContext.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        
        _logger.LogInformation("📄 COMPLETE CONTEXT BEING SENT TO LLM:");
        _logger.LogInformation("────────────────────────────────────────────────────────────");
        _logger.LogInformation("{Context}", finalContext);
        _logger.LogInformation("────────────────────────────────────────────────────────────");
        _logger.LogInformation("✅ RAG context prepared in {TotalMs}ms - ready for LLM", overallStopwatch.ElapsedMilliseconds);
        _logger.LogInformation("════════════════════════════════════════════════════════════");

        return finalContext;
    }

    // Helper methods for git clone-based indexing

    /// <summary>
    /// Configure git authentication for Azure DevOps using PAT token
    /// </summary>
    private async Task<bool> ConfigureGitAuthAsync(string workingDirectory)
    {
        try
        {
            var pat = Environment.GetEnvironmentVariable("ADO_PAT");
            if (string.IsNullOrEmpty(pat))
            {
                _logger.LogWarning("⚠️ ADO_PAT environment variable not set. Git clone may require authentication.");
                return false;
            }

            // Configure git credential helper to use the PAT token for Azure DevOps
            var configResult = await RunGitCommandAsync(
                $"config credential.https://dev.azure.com.helper store", 
                workingDirectory, 
                30
            );

            if (!configResult.Success)
            {
                _logger.LogWarning("⚠️ Failed to configure git credential helper: {Error}", configResult.Error);
                return false;
            }

            _logger.LogInformation("✅ Git authentication configured for Azure DevOps");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Exception configuring git authentication");
            return false;
        }
    }

    /// <summary>
    /// Build authenticated clone URL for Azure DevOps
    /// </summary>
    private string BuildAuthenticatedCloneUrl(string originalUrl)
    {
        var pat = _adoClient.PersonalAccessToken;
        if (string.IsNullOrEmpty(pat))
        {
            return originalUrl; // Return original URL if no PAT available
        }

        try
        {
            var uri = new Uri(originalUrl);
            if (uri.Host.Contains("dev.azure.com"))
            {
                // Convert dev.azure.com URL to visualstudio.com format for better compatibility
                // Original: https://dev.azure.com/organization/project/_git/repository
                // Target:   https://git:pat@organization.visualstudio.com/DefaultCollection/project/_git/repository
                
                var pathParts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (pathParts.Length >= 3)
                {
                    var organization = pathParts[0]; // "organization"
                    var project = pathParts[1]; // "project"
                    var repoPath = string.Join("/", pathParts.Skip(2)); // "_git/repository"
                    
                    // Use visualstudio.com format with username and DefaultCollection
                    var authenticatedUrl = $"https://git:{pat}@{organization.ToLower()}.visualstudio.com/DefaultCollection/{project}/{repoPath}";
                    _logger.LogDebug("🔐 Using authenticated clone URL for Azure DevOps (visualstudio.com format)");
                    return authenticatedUrl;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Failed to build authenticated URL, using original");
        }

        return originalUrl;
    }

    /// <summary>
    /// Enhanced directory cleanup that handles git objects and locks properly
    /// </summary>
    private async Task<bool> CleanupCloneDirectoryAsync(string cloneDirectory)
    {
        try
        {
            if (!Directory.Exists(cloneDirectory))
            {
                _logger.LogDebug("🧹 Clone directory doesn't exist, nothing to clean");
                return true;
            }

            _logger.LogInformation("🧹 Cleaning up clone directory: {Directory}", cloneDirectory);

            // First, try to remove git locks if they exist
            await RemoveGitLocksAsync(cloneDirectory);

            // Try normal deletion first
            try
            {
                Directory.Delete(cloneDirectory, recursive: true);
                _logger.LogInformation("✅ Successfully deleted clone directory");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Fall back to PowerShell force removal for git objects
                await ForceDeleteDirectoryAsync(cloneDirectory);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogDebug("🧹 Directory already deleted");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Exception during directory cleanup");
            return false;
        }
    }

    /// <summary>
    /// Remove git lock files that might prevent directory deletion
    /// </summary>
    private async Task RemoveGitLocksAsync(string directory)
    {
        try
        {
            var gitDir = Path.Combine(directory, ".git");
            if (!Directory.Exists(gitDir))
                return;

            // Common git lock files
            var lockFiles = new[]
            {
                Path.Combine(gitDir, "index.lock"),
                Path.Combine(gitDir, "HEAD.lock"),
                Path.Combine(gitDir, "config.lock"),
                Path.Combine(gitDir, "refs", "heads", "master.lock"),
                Path.Combine(gitDir, "refs", "heads", "main.lock")
            };

            foreach (var lockFile in lockFiles)
            {
                if (File.Exists(lockFile))
                {
                    try
                    {
                        File.Delete(lockFile);
                        _logger.LogDebug("🔓 Removed git lock file: {LockFile}", lockFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "⚠️ Failed to remove lock file: {LockFile}", lockFile);
                    }
                }
            }

            await Task.Delay(100); // Brief pause to let filesystem settle
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "⚠️ Exception removing git locks");
        }
    }

    private async Task<(bool Success, string Error)> RunGitCommandAsync(string arguments, string workingDirectory, int timeoutSeconds = 180)
    {
        try
        {
            var safeArguments = RedactSecrets(arguments);

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            _logger.LogInformation("🔧 Running git command: git {Arguments}", safeArguments);
            _logger.LogInformation("   Timeout: {TimeoutSeconds} seconds", timeoutSeconds);
            
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            process.Start();

            // Create cancellation token for timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            
            try
            {
                // Use Task.WhenAny for proper timeout handling
                var processTask = process.WaitForExitAsync();
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                
                var completedTask = await Task.WhenAny(processTask, timeoutTask);
                stopwatch.Stop();

                if (completedTask == timeoutTask)
                {
                    // Timeout occurred
                    _logger.LogWarning("⚠️ Git command timed out after {TimeoutSeconds} seconds", timeoutSeconds);
                    
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync();
                            _logger.LogInformation("🔪 Killed git process due to timeout");
                        }
                    }
                    catch (Exception killEx)
                    {
                        _logger.LogWarning(killEx, "⚠️ Failed to kill git process");
                    }
                    
                    return (false, $"Git command timed out after {timeoutSeconds} seconds");
                }

                // Process completed within timeout
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                if (process.ExitCode != 0)
                {
                    _logger.LogError("❌ Git command failed with exit code {ExitCode} after {ElapsedMs}ms", 
                        process.ExitCode, stopwatch.ElapsedMilliseconds);
                    _logger.LogError("Error output: {Error}", error);
                    return (false, error);
                }

                _logger.LogInformation("✅ Git command completed successfully in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
                if (!string.IsNullOrEmpty(output))
                {
                    _logger.LogDebug("Command output: {Output}", output);
                }

                return (true, string.Empty);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⚠️ Git command timed out after {TimeoutSeconds} seconds", timeoutSeconds);
                
                // Kill the process if it's still running
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                        _logger.LogInformation("🔪 Killed git process due to timeout");
                    }
                }
                catch (Exception killEx)
                {
                    _logger.LogWarning(killEx, "⚠️ Failed to kill git process");
                }
                
                return (false, $"Git command timed out after {timeoutSeconds} seconds");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Exception running git command: git {Arguments}", RedactSecrets(arguments));
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Redact sensitive values (PATs/passwords) from logged command arguments.
    /// </summary>
    private string RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = text;

        // Redact credential section in URLs: https://user:secret@host -> https://user:***@host
        redacted = Regex.Replace(redacted, @"(https?://[^:/\s]+:)([^@\s]+)(@)", "$1***$3", RegexOptions.IgnoreCase);

        // Redact current PAT explicitly if present.
        var pat = _adoClient.PersonalAccessToken;
        if (!string.IsNullOrEmpty(pat))
        {
            redacted = redacted.Replace(pat, "***", StringComparison.Ordinal);
        }

        return redacted;
    }

    private bool IsGitFile(string filePath)
    {
        return filePath.Contains(".git" + Path.DirectorySeparatorChar) || 
               filePath.Contains(".git" + Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Generate embedding with exponential backoff retry for rate limits
    /// </summary>
    private async Task<object> GenerateEmbeddingWithRetryAsync(string content, int maxRetries = 3)
    {
        int attempt = 0;
        Exception lastException = null!;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation("🤖 CALLING AI EMBEDDING API (attempt {Attempt}/{MaxRetries})", 1, maxRetries);
        _logger.LogInformation("   Content length: {Length} chars", content.Length);
        _logger.LogInformation("   Content preview: {Preview}", 
            content.Length > 100 ? content.Substring(0, 100).Replace("\n", "\\n") + "..." : content.Replace("\n", "\\n"));

        while (attempt < maxRetries)
        {
            try
            {
                attempt++;
                _logger.LogInformation("🔄 AI Embedding API call #{Attempt} starting...", attempt);
                var result = await _embeddingGenerator.GenerateAsync(content);
                stopwatch.Stop();
                
                _logger.LogInformation("✅ AI Embedding API call #{Attempt} succeeded in {ElapsedMs}ms", attempt, stopwatch.ElapsedMilliseconds);
                var vectorLength = ((dynamic)result).Vector.Length;
                _logger.LogInformation("   Vector dimension: {Dim}", (int)vectorLength);
                return result;
            }
            catch (Exception ex) when (IsRateLimitException(ex) && attempt < maxRetries)
            {
                lastException = ex;
                var delayMs = Math.Min(1000 * (int)Math.Pow(2, attempt - 1), 30000); // Cap at 30 seconds
                
                _logger.LogWarning("⏳ AI Embedding API rate limit hit (attempt {Attempt}/{MaxRetries}). Waiting {DelayMs}ms before retry...", 
                    attempt, maxRetries, delayMs);
                _logger.LogWarning("   Error: {Error}", ex.Message);
                    
                await Task.Delay(delayMs);
            }
            catch (Exception ex) when (IsInvalidRequestException(ex))
            {
                stopwatch.Stop();
                _logger.LogError("❌ AI Embedding API invalid request (attempt {Attempt}): {Error}", attempt, ex.Message);
                _logger.LogError("   Failed after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogError("❌ AI Embedding API call #{Attempt} failed: {Error}", attempt, ex.Message);
                if (attempt >= maxRetries)
                {
                    stopwatch.Stop();
                    _logger.LogError("   All {MaxRetries} attempts failed after {ElapsedMs}ms", maxRetries, stopwatch.ElapsedMilliseconds);
                }
            }
        }

        throw lastException;
    }

    private static bool IsRateLimitException(Exception ex)
    {
        var message = ex.Message?.ToLowerInvariant() ?? "";
        return message.Contains("rate limit") || 
               message.Contains("429") || 
               message.Contains("ratelimitreached") ||
               message.Contains("quota");
    }

    private static bool IsInvalidRequestException(Exception ex)
    {
        var message = ex.Message?.ToLowerInvariant() ?? "";
        return message.Contains("400") || message.Contains("invalid_request_error");
    }

    private async Task<List<CodeChunk>> ProcessFilesFromDiskAsync(string cloneDir, List<string> filePaths)
    {
        var chunks = new List<CodeChunk>();
        int indexed = 0;
        int skipped = 0;
        int failed = 0;

        _logger.LogInformation("📁 Processing {FileCount} files from disk...", filePaths.Count);

        foreach (var relativePath in filePaths)
        {
            // Skip binary files, tests, generated code
            if (ShouldSkipFile(relativePath))
            {
                skipped++;
                _logger.LogDebug("⏭️  Skipped (pattern match): {FilePath}", relativePath);
                continue;
            }

            var fullPath = Path.Combine(cloneDir, relativePath);
            
            try
            {
                // Check file size - skip very large files (>1MB)
                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Length > 1024 * 1024) // 1MB
                {
                    skipped++;
                    _logger.LogDebug("⏭️  Skipped (too large {Size} bytes): {FilePath}", fileInfo.Length, relativePath);
                    continue;
                }

                _logger.LogInformation("📄 Processing file: {FilePath} ({Size} bytes)", relativePath, fileInfo.Length);

                // Read file content from disk
                string content;
                try
                {
                    content = await File.ReadAllTextAsync(fullPath);
                }
                catch (Exception readEx)
                {
                    // Try with different encoding if UTF-8 fails
                    _logger.LogWarning("⚠️  UTF-8 read failed for {FilePath}, trying with default encoding: {Error}", relativePath, readEx.Message);
                    content = File.ReadAllText(fullPath, System.Text.Encoding.Default);
                }

                if (string.IsNullOrWhiteSpace(content) || content.Length < 10)
                {
                    skipped++;
                    _logger.LogDebug("⏭️  Skipped (empty or too small): {FilePath}", relativePath);
                    continue;
                }

                _logger.LogInformation("   📊 File analysis: {Length} characters, {Lines} lines", 
                    content.Length, content.Split('\n').Length);

                // Split into chunks
                var fileChunks = SplitIntoChunks(content, relativePath);
                _logger.LogInformation("   📦 Created {ChunkCount} chunks", fileChunks.Count);

                // Generate embeddings for each chunk
                foreach (var chunk in fileChunks)
                {
                    _logger.LogInformation("  🔤 CHUNK CONTENT for chunk {Index} (lines {Start}-{End}):",
                        chunk.ChunkIndex, chunk.StartLine, chunk.EndLine);
                    _logger.LogInformation("     Preview (first 200 chars): {Content}",
                        chunk.Content.Length > 200 ? chunk.Content.Substring(0, 200).Replace("\n", "\\n") + "..." : chunk.Content.Replace("\n", "\\n"));
                    _logger.LogInformation("     Full content length: {Length} characters", chunk.Content.Length);

                    _logger.LogInformation("  🧮 Generating embedding for chunk {Index}...", chunk.ChunkIndex);

                    try
                    {
                        // Generate embedding for the chunk with retry logic
                        var embeddingResponse = await GenerateEmbeddingWithRetryAsync(chunk.Content);
                        chunk.Embedding = ((dynamic)embeddingResponse).Vector.ToArray();
                        
                        _logger.LogInformation("  📊 EMBEDDING VECTOR DETAILS for chunk {Index}:", chunk.ChunkIndex);
                        _logger.LogInformation("     Dimension: {Dim}", chunk.Embedding.Length);
                        _logger.LogInformation("     First 5 values: [{Values}]", 
                            string.Join(", ", chunk.Embedding.Take(5).Select(v => v.ToString("F6"))));
                        _logger.LogInformation("     Last 5 values: [{Values}]", 
                            string.Join(", ", chunk.Embedding.TakeLast(5).Select(v => v.ToString("F6"))));
                        _logger.LogInformation("     Min/Max: [{Min:F6}, {Max:F6}]", chunk.Embedding.Min(), chunk.Embedding.Max());
                        _logger.LogInformation("     Mean: {Mean:F6}", chunk.Embedding.Average());
                        
                        chunks.Add(chunk);
                        indexed++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogError("❌ Failed to generate embedding for chunk {Index} from {FilePath}: {Error}", 
                            chunk.ChunkIndex, relativePath, ex.Message);
                        _logger.LogInformation("⏭️  Skipping problematic chunk and continuing with next file...");
                        continue; // Skip to next chunk
                    }

                    _logger.LogInformation("  ✅ Chunk {Index} indexed successfully", chunk.ChunkIndex);
                }

                _logger.LogInformation("✅ Indexed {FilePath} ({ChunkCount} chunks)",
                    relativePath, fileChunks.Count);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "❌ Error processing file: {FilePath}", relativePath);
            }
        }

        _logger.LogInformation("📊 FILE PROCESSING SUMMARY (Git Clone Method):");
        _logger.LogInformation("   Files discovered: {Total}", filePaths.Count);
        _logger.LogInformation("   Files indexed: {Indexed}", indexed > 0 ? chunks.Select(c => c.FilePath).Distinct().Count() : 0);
        _logger.LogInformation("   Files skipped: {Skipped}", skipped);
        _logger.LogInformation("   Files failed: {Failed}", failed);
        _logger.LogInformation("   Total chunks created: {ChunkCount}", chunks.Count);

        return chunks;
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
        
        // Conservative token limits to stay well under 8192 token limit
        const int MAX_TOKENS_PER_CHUNK = 6000; // Leave buffer for prompt overhead
        const int ESTIMATED_TOKENS_PER_CHAR = 4; // Conservative estimate: ~4 chars per token
        const int MAX_CHARS_PER_CHUNK = MAX_TOKENS_PER_CHUNK / ESTIMATED_TOKENS_PER_CHAR; // ~1500 chars
        const int OVERLAP_LINES = 5; // Reduced overlap for larger files

        int startLine = 0;
        while (startLine < lines.Length)
        {
            var chunkLines = new List<string>();
            var currentChars = 0;
            var currentLineIndex = startLine;
            
            // Build chunk within character/token limit
            while (currentLineIndex < lines.Length && currentChars < MAX_CHARS_PER_CHUNK)
            {
                var line = lines[currentLineIndex];
                // Check if adding this line would exceed limit
                if (currentChars + line.Length + 1 > MAX_CHARS_PER_CHUNK && chunkLines.Count > 0)
                    break;
                    
                chunkLines.Add(line);
                currentChars += line.Length + 1; // +1 for newline
                currentLineIndex++;
            }
            
            // Ensure we make progress even with very long lines
            if (chunkLines.Count == 0 && currentLineIndex < lines.Length)
            {
                // Take just this one long line, but truncate it if necessary
                var longLine = lines[currentLineIndex];
                if (longLine.Length > MAX_CHARS_PER_CHUNK)
                {
                    longLine = longLine.Substring(0, MAX_CHARS_PER_CHUNK) + "... [TRUNCATED]";
                }
                chunkLines.Add(longLine);
                currentLineIndex++;
            }
            
            if (chunkLines.Count == 0) break; // Safety check
            
            var chunkContent = string.Join('\n', chunkLines);
            chunks.Add(new CodeChunk
            {
                Content = chunkContent,
                ChunkIndex = chunks.Count,
                StartLine = startLine + 1,
                EndLine = startLine + chunkLines.Count,
                Metadata = $"{filePath}:L{startLine + 1}-L{startLine + chunkLines.Count} ({chunkContent.Length} chars, ~{chunkContent.Length * ESTIMATED_TOKENS_PER_CHAR} tokens)",
                FilePath = filePath,
                Embedding = Array.Empty<float>() // Will be filled during indexing
            });
            
            // Next chunk starts with overlap
            startLine = Math.Max(currentLineIndex - OVERLAP_LINES, currentLineIndex);
            if (startLine >= currentLineIndex) break; // Prevent infinite loop
        }

        return chunks;
    }

    private string BuildSearchQuery(PullRequestFile file)
    {
        _logger.LogDebug("🔧 BUILD SEARCH QUERY:");
        _logger.LogDebug("   Input file: {FilePath}", file.Path);
        _logger.LogDebug("   Diff available: {HasDiff}", !string.IsNullOrEmpty(file.UnifiedDiff));

        var queryParts = new List<string>();

        // Extract meaningful content from changes
        if (!string.IsNullOrEmpty(file.UnifiedDiff))
        {
            _logger.LogDebug("   Processing unified diff ({Length} chars)...", file.UnifiedDiff.Length);

            var allDiffLines = file.UnifiedDiff.Split('\n');
            var addedLines = allDiffLines
                .Where(l => l.StartsWith('+') && !l.StartsWith("+++"))
                .ToList();

            _logger.LogDebug("   Total lines in diff: {Total}", allDiffLines.Length);
            _logger.LogDebug("   Added lines (starting with '+'): {Added}", addedLines.Count);

            var diffLines = addedLines
                .Select(l => l.Substring(1).Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length > 5)
                .Take(10) // First 10 added lines
                .ToList();

            _logger.LogDebug("   Selected lines for query (non-empty, >5 chars, max 10): {Count}", diffLines.Count);
            foreach (var line in diffLines.Take(5))
            {
                _logger.LogDebug("     - \"{Line}\"", line.Length > 60 ? line.Substring(0, 60) + "..." : line);
            }

            queryParts.AddRange(diffLines);
        }

        // Add file name context
        var fileName = Path.GetFileNameWithoutExtension(file.Path);
        queryParts.Add($"file {fileName}");
        _logger.LogDebug("   Added file name context: 'file {FileName}'", fileName);

        var query = string.Join(' ', queryParts);
        var finalQuery = query.Length > 1000 ? query.Substring(0, 1000) : query;

        _logger.LogDebug("   Final query length: {Length} chars (max 1000)", finalQuery.Length);
        return finalQuery;
    }

    private string BuildHybridSearchQuery(PullRequestFile file, string diffQuery)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(diffQuery))
        {
            parts.Add(diffQuery);
        }

        var normalizedPath = (file.Path ?? string.Empty).Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            parts.Add($"path {normalizedPath}");

            var pathParts = normalizedPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(5)
                .ToList();

            if (pathParts.Count > 0)
            {
                parts.Add($"package {string.Join(' ', pathParts)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(file.Content))
        {
            var contentLines = file.Content
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 3)
                .Take(40)
                .ToList();

            if (contentLines.Count > 0)
            {
                parts.Add(string.Join(' ', contentLines));
            }
        }

        var query = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return query.Length > 1500 ? query.Substring(0, 1500) : query;
    }

    private List<SimilarityResult> GetPathBasedFallbackChunks(
        PullRequestFile file,
        List<CodeChunk> chunks,
        int maxResults)
    {
        var targetPath = (file.Path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        var fileName = Path.GetFileNameWithoutExtension(targetPath);
        var dir = Path.GetDirectoryName(targetPath)?.Replace('\\', '/').ToLowerInvariant() ?? string.Empty;

        return chunks
            .Select(chunk =>
            {
                var chunkPath = (chunk.FilePath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
                var score = 0.0;

                if (!string.IsNullOrWhiteSpace(fileName) && chunkPath.Contains(fileName))
                {
                    score += 3.0;
                }

                if (!string.IsNullOrWhiteSpace(dir))
                {
                    var dirParts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    score += dirParts.Count(part => chunkPath.Contains(part)) * 0.2;
                }

                if (chunkPath.EndsWith(".java", StringComparison.OrdinalIgnoreCase) && targetPath.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                {
                    score += 0.3;
                }

                return new SimilarityResult
                {
                    Chunk = chunk,
                    Similarity = score
                };
            })
            .Where(r => r.Similarity > 0)
            .OrderByDescending(r => r.Similarity)
            .Take(maxResults)
            .ToList();
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

    private string GetRelatedTestContext(
        PullRequestFile file,
        string repositoryId,
        int maxFiles,
        int maxChunksPerFile)
    {
        if (!_inMemoryStore.TryGetValue(repositoryId, out var allChunks) || allChunks.Count == 0)
        {
            return string.Empty;
        }

        var targetFileName = Path.GetFileNameWithoutExtension(file.Path).ToLowerInvariant();
        var targetDirParts = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.ToLowerInvariant())
            .ToHashSet();

        var testFiles = allChunks
            .Select(c => c.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(IsTestFilePath)
            .Select(path => new
            {
                Path = path,
                Score = ScoreTestFileCandidate(path, targetFileName, targetDirParts)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Take(maxFiles)
            .ToList();

        if (testFiles.Count == 0)
        {
            return string.Empty;
        }

        var context = new StringBuilder();
        context.AppendLine("## Related Test Context");
        context.AppendLine();

        foreach (var testFile in testFiles)
        {
            var chunksForFile = allChunks
                .Where(c => string.Equals(c.FilePath, testFile.Path, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.ChunkIndex)
                .Take(maxChunksPerFile)
                .ToList();

            if (chunksForFile.Count == 0)
            {
                continue;
            }

            context.AppendLine($"### {testFile.Path} (score: {testFile.Score})");
            context.AppendLine("```");
            foreach (var chunk in chunksForFile)
            {
                context.AppendLine(chunk.Content.Length > 400
                    ? chunk.Content.Substring(0, 400) + "..."
                    : chunk.Content);
                context.AppendLine();
            }
            context.AppendLine("```");
            context.AppendLine();
        }

        return context.ToString();
    }

    private static bool IsTestFilePath(string path)
    {
        var p = path.ToLowerInvariant();
        return p.Contains("/test/") ||
               p.Contains("/tests/") ||
               p.EndsWith("test.cs") ||
               p.EndsWith("tests.cs") ||
               p.EndsWith("_test.cs") ||
               p.EndsWith("_tests.cs");
    }

    private static int ScoreTestFileCandidate(string testPath, string targetFileName, HashSet<string> targetDirParts)
    {
        var score = 0;
        var testLower = testPath.ToLowerInvariant();

        if (testLower.Contains(targetFileName))
        {
            score += 5;
        }

        var normalizedTarget = targetFileName
            .Replace("controller", "")
            .Replace("service", "")
            .Replace("manager", "")
            .Trim();

        if (!string.IsNullOrWhiteSpace(normalizedTarget) && testLower.Contains(normalizedTarget))
        {
            score += 3;
        }

        var testParts = testLower.Split('/', StringSplitOptions.RemoveEmptyEntries);
        score += testParts.Count(part => targetDirParts.Contains(part));

        return score;
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

    private static float[] ToFloatVector(dynamic vector)
    {
        if (vector is null)
        {
            return Array.Empty<float>();
        }

        if (vector is float[] floatArray)
        {
            return floatArray;
        }

        if (vector is IEnumerable<float> floatEnumerable)
        {
            return floatEnumerable.ToArray();
        }

        if (vector is double[] doubleArray)
        {
            return doubleArray.Select(v => (float)v).ToArray();
        }

        if (vector is IEnumerable<double> doubleEnumerable)
        {
            return doubleEnumerable.Select(v => (float)v).ToArray();
        }

        try
        {
            return ((IEnumerable<object>)vector).Select(v => Convert.ToSingle(v)).ToArray();
        }
        catch
        {
            return Array.Empty<float>();
        }
    }

    /// <summary>
    /// Force delete directory including Git objects with read-only permissions
    /// </summary>
    private async Task ForceDeleteDirectoryAsync(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await RemoveGitLocksAsync(directoryPath);

                // First try normal deletion
                Directory.Delete(directoryPath, true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                _logger.LogDebug("🔒 Delete attempt {Attempt}/{MaxAttempts} hit access issue, retrying...", attempt, maxAttempts);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                _logger.LogDebug("🔒 Delete attempt {Attempt}/{MaxAttempts} hit IO lock issue, retrying...", attempt, maxAttempts);
            }

            // Git objects or lock files may still be releasing handles; try force remove then retry.
            using (var process = new System.Diagnostics.Process())
            {
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-Command \"Remove-Item '{directoryPath}' -Recurse -Force -ErrorAction SilentlyContinue\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                process.Start();
                await process.WaitForExitAsync();
            }

            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            await Task.Delay(250 * attempt);
        }

        // Final attempt throws if deletion still fails so callers can log accurately.
        Directory.Delete(directoryPath, true);
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

public class SimilarityResult
{
    public CodeChunk Chunk { get; set; } = new();
    public double Similarity { get; set; }
}
