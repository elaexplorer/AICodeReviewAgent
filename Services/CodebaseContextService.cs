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
    private readonly EmbeddingPersistenceService _persistence;
    private readonly Dictionary<string, List<CodeChunk>> _inMemoryStore;
    private const string COLLECTION_NAME = "codebase";
    private const int CloneTimeoutSeconds = 60;

    public CodebaseContextService(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        AzureDevOpsRestClient adoClient,
        EmbeddingPersistenceService persistence,
        ILogger<CodebaseContextService> logger)
    {
        _embeddingGenerator = embeddingGenerator;
        _adoClient = adoClient;
        _persistence = persistence;
        _logger = logger;
        _inMemoryStore = new Dictionary<string, List<CodeChunk>>();
    }

    /// <summary>
    /// Reload a repository's chunks from the SQLite persistence store into memory.
    /// Called by IndexRestoreHostedService on startup.
    /// </summary>
    public async Task RestoreIndexFromDiskAsync(string repositoryId)
    {
        var chunks = await _persistence.LoadAsync(repositoryId);
        if (chunks == null || chunks.Count == 0)
        {
            _logger.LogInformation("📦 No persisted chunks found for '{Repo}'", repositoryId);
            return;
        }
        _inMemoryStore[repositoryId] = chunks;
        _logger.LogInformation("✅ Restored {Count} chunks for '{Repo}' from SQLite", chunks.Count, repositoryId);
    }

    /// <summary>
    /// Incrementally refresh the index for a repository.
    /// Fetches the latest commits, re-embeds only changed files, and persists.
    /// Returns the number of chunks updated (0 = already up-to-date, -1 = fell back to full re-index).
    /// </summary>
    public async Task<int> RefreshIndexAsync(
        string project,
        string repositoryId,
        string branch = "master",
        string? accessTokenOverride = null)
    {
        _logger.LogInformation("🔄 RefreshIndex: Starting incremental refresh for '{Repo}' branch '{Branch}'", repositoryId, branch);

        var tempReposDir = Environment.GetEnvironmentVariable("RagRuntime__TempReposRootPath")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "temp_repos");
        var tempDir  = Path.Combine(tempReposDir, repositoryId);
        var cloneDir = Path.Combine(tempDir, "repo");

        // If no clone on disk, fall back to a full re-index
        if (!Directory.Exists(cloneDir) || !Directory.Exists(Path.Combine(cloneDir, ".git")))
        {
            _logger.LogInformation("📂 No local clone found — falling back to full re-index");
            await IndexRepositoryWithCloneAsync(project, repositoryId, branch, null, accessTokenOverride);
            return -1;
        }

        // Retrieve the hash we indexed last time
        var storedHash = await _persistence.GetCommitHashAsync(repositoryId);
        _logger.LogInformation("📌 Stored commit hash: {Hash}", storedHash ?? "(none)");

        // Fetch latest from remote (shallow, up to 50 commits so we can diff)
        var fetchResult = await RunGitCommandAsync($"fetch origin {branch} --depth=50", cloneDir, 60);
        if (!fetchResult.Success)
        {
            _logger.LogWarning("⚠️ git fetch failed: {Error} — falling back to full re-index", fetchResult.Error);
            await IndexRepositoryWithCloneAsync(project, repositoryId, branch, null, accessTokenOverride);
            return -1;
        }

        // Resolve FETCH_HEAD hash
        var fetchHeadResult = await RunGitCommandAsync("rev-parse FETCH_HEAD", cloneDir, 10);
        var currentHash = fetchHeadResult.Success ? fetchHeadResult.Output.Trim() : null;
        _logger.LogInformation("📌 FETCH_HEAD hash: {Hash}", currentHash ?? "(unknown)");

        if (!string.IsNullOrWhiteSpace(storedHash) &&
            string.Equals(storedHash, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("✅ Index is already up-to-date (hash={Hash})", storedHash);
            return 0;
        }

        // Get the list of changed files
        List<string> changedFiles;
        if (!string.IsNullOrWhiteSpace(storedHash))
        {
            var diffResult = await RunGitCommandAsync($"diff --name-only {storedHash} FETCH_HEAD", cloneDir, 30);
            if (!diffResult.Success)
            {
                _logger.LogWarning("⚠️ git diff failed — falling back to full re-index");
                await IndexRepositoryWithCloneAsync(project, repositoryId, branch, null, accessTokenOverride);
                return -1;
            }
            changedFiles = diffResult.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();
        }
        else
        {
            var lsResult = await RunGitCommandAsync("ls-files", cloneDir, 30);
            changedFiles = lsResult.Success
                ? lsResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).ToList()
                : new List<string>();
        }

        _logger.LogInformation("📝 {Count} files changed since last index", changedFiles.Count);

        if (changedFiles.Count == 0)
        {
            _logger.LogInformation("✅ No changed files — index is current");
            return 0;
        }

        // Advance the local branch to FETCH_HEAD
        var resetResult = await RunGitCommandAsync("reset --hard FETCH_HEAD", cloneDir, 30);
        if (!resetResult.Success)
        {
            _logger.LogWarning("⚠️ git reset failed — falling back to full re-index");
            await IndexRepositoryWithCloneAsync(project, repositoryId, branch, null, accessTokenOverride);
            return -1;
        }

        // Re-embed only the changed files that still exist on disk
        var existingChanged = changedFiles
            .Where(f => File.Exists(Path.Combine(cloneDir, f)))
            .ToList();

        _logger.LogInformation("🔢 Re-embedding {Count} files…", existingChanged.Count);
        var newChunks = await ProcessFilesFromDiskAsync(cloneDir, existingChanged);

        // Merge into in-memory store
        var normalizedChanged = changedFiles
            .Select(f => f.Replace('\\', '/').TrimStart('/').ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (_inMemoryStore.TryGetValue(repositoryId, out var existing))
        {
            var kept = existing.Where(c =>
                !normalizedChanged.Contains(c.FilePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant()))
                .ToList();
            kept.AddRange(newChunks);
            _inMemoryStore[repositoryId] = kept;
        }
        else
        {
            _inMemoryStore[repositoryId] = newChunks;
        }

        var totalChunks = _inMemoryStore[repositoryId].Count;
        _logger.LogInformation("✅ Refresh complete: {New} new chunks, {Total} total", newChunks.Count, totalChunks);

        _ = _persistence.SaveAsync(repositoryId, _inMemoryStore[repositoryId], currentHash);

        return newChunks.Count;
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
        string? repositoryUrl = null,
        string? accessTokenOverride = null)
    {
        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║ RAG INDEXING: Starting Git Clone-Based Repository Indexing ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
        _logger.LogInformation("Repository ID: {RepositoryId}", repositoryId);
        _logger.LogInformation("Project: {Project}", project);
        _logger.LogInformation("Branch: {Branch}", branch);

        // Step 1: Prepare clone directory — prefer mounted persistent path if set
        var currentDir = Directory.GetCurrentDirectory();
        var tempReposDir = Environment.GetEnvironmentVariable("RagRuntime__TempReposRootPath")
            ?? Path.Combine(currentDir, "temp_repos");
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
            repoUrl = BuildAuthenticatedCloneUrl(repoUrl, accessTokenOverride);
            var adoPat = _adoClient.PersonalAccessToken;
            var normalizedOverride = NormalizeAdoAccessToken(accessTokenOverride);
            var logUrl = repoUrl;
            if (!string.IsNullOrEmpty(adoPat))
            {
                logUrl = logUrl.Replace(adoPat, "***");
            }
            if (!string.IsNullOrEmpty(normalizedOverride))
            {
                logUrl = logUrl.Replace(normalizedOverride, "***");
            }
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
            // Step 3: Capture HEAD commit hash for incremental refresh
            var headHashResult = await RunGitCommandAsync("rev-parse HEAD", cloneDir, 10);
            var headCommitHash = headHashResult.Success ? headHashResult.Output.Trim() : null;
            if (!string.IsNullOrWhiteSpace(headCommitHash))
                _logger.LogInformation("📌 HEAD commit hash: {Hash}", headCommitHash);

            // Step 4: Discover all files using file system
            _logger.LogInformation("Step 4: Discovering files using file system traversal...");
            var allFiles = Directory.GetFiles(cloneDir, "*.*", SearchOption.AllDirectories)
                .Where(f => !IsGitFile(f)) // Skip .git directory files
                .Select(f => Path.GetRelativePath(cloneDir, f).Replace('\\', '/')) // Normalize to forward slashes
                .ToList();

            _logger.LogInformation("Found {FileCount} total files via file system (vs API discovery)", allFiles.Count);

            // Step 5: Process files with actual content from disk
            var chunks = await ProcessFilesFromDiskAsync(cloneDir, allFiles);

            // Step 6: Store in memory index and persist to SQLite (with commit hash)
            _inMemoryStore[repositoryId] = chunks;
            _ = _persistence.SaveAsync(repositoryId, chunks, headCommitHash); // fire-and-forget

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
        // Clone directory is intentionally preserved so RefreshIndexAsync can do
        // incremental git fetch + diff instead of a full re-clone next time.
    }

    /// <summary>
    /// Index the entire repository for semantic search using Git Clone only
    /// Run this once when PR is opened, or periodically
    /// </summary>
    public async Task<int> IndexRepositoryAsync(
        string project,
        string repositoryId,
        string branch = "master",
        string? accessTokenOverride = null)
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
            var cloneResult = await IndexRepositoryWithCloneAsync(project, repositoryId, branch, null, accessTokenOverride);
            if (cloneResult > 0)
            {
                _logger.LogInformation("✅ Git Clone approach succeeded: {ChunkCount} chunks indexed", cloneResult);
                return cloneResult;
            }
            else
            {
                _logger.LogWarning("⚠️ Git Clone approach returned 0 chunks - attempting API fallback indexing...");

                var (apiSuccess, apiIndexedCount, apiError) = await TryIndexWithApiAsync(project, repositoryId, branch);
                if (apiSuccess && apiIndexedCount > 0)
                {
                    _logger.LogInformation("✅ API fallback succeeded: {ChunkCount} chunks indexed", apiIndexedCount);
                    return apiIndexedCount;
                }

                var message = $"RAG repository indexing failed for {project}/{repositoryId} on branch '{branch}'. Clone returned 0 chunks and API fallback failed. API error: {apiError ?? "none"}";
                _logger.LogError("❌ {Message}", message);
                throw new InvalidOperationException(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Git Clone approach failed - attempting API fallback...");

            var (apiSuccess, apiIndexedCount, apiError) = await TryIndexWithApiAsync(project, repositoryId, branch);
            if (apiSuccess && apiIndexedCount > 0)
            {
                _logger.LogInformation("✅ API fallback succeeded after clone exception: {ChunkCount} chunks indexed", apiIndexedCount);
                return apiIndexedCount;
            }

            var message = $"RAG repository indexing failed for {project}/{repositoryId} on branch '{branch}'. Clone error: {ex.Message}. API fallback error: {apiError ?? "none"}";
            _logger.LogError("❌ {Message}", message);
            throw new InvalidOperationException(message, ex);
        }
    }

    /// <summary>
    /// Try indexing using Azure DevOps REST API
    /// </summary>
    private async Task<(bool Success, int IndexedCount, string? Error)> TryIndexWithApiAsync(
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
                return (false, 0, "No files found in repository via API method.");
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
                        var langTag = GetLanguageTag(chunk.FilePath);
                        var textToEmbed = string.IsNullOrEmpty(langTag) ? chunk.Content : $"{langTag}\n{chunk.Content}";
                        var embeddingResponse = await _embeddingGenerator.GenerateAsync(textToEmbed);
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
                return (false, 0, $"Too many API retrieval errors during validation batch ({apiErrors}/{apiTestFiles.Count}).");
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
                            var langTag = GetLanguageTag(chunk.FilePath);
                            var textToEmbed = string.IsNullOrEmpty(langTag) ? chunk.Content : $"{langTag}\n{chunk.Content}";
                            var embedding = await GenerateEmbeddingWithRetryAsync(textToEmbed);
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
            
            // Store all chunks in memory and persist to SQLite
            _inMemoryStore[repositoryId] = chunks;
            _ = _persistence.SaveAsync(repositoryId, chunks); // fire-and-forget

            _logger.LogInformation("✅ API Method: Indexed {Count} chunks from {Files} files", indexed, files.Count);
            return (true, indexed, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ API indexing method failed");
            return (false, 0, ex.Message);
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
            var queryVector = ExtractEmbeddingVector(queryEmbeddingResponse);

            if (queryVector.Length == 0)
            {
                _logger.LogWarning("⚠️ Query embedding vector is empty for {FilePath}; skipping semantic retrieval", file.Path);
                return string.Empty;
            }

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
                        var hybridVector = ExtractEmbeddingVector(hybridEmbeddingResponse);

                        if (hybridVector.Length == 0)
                        {
                            _logger.LogWarning("⚠️ Hybrid query embedding vector is empty; skipping hybrid semantic retry");
                            hybridVector = Array.Empty<float>();
                        }

                        var hybridSimilarities = hybridVector.Length == 0
                            ? new List<SimilarityResult>()
                            : chunks
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

                // Reconstruct the full file from all its chunks (sorted by line number)
                const int MAX_FULL_FILE_CHARS = 8000; // ~2000 tokens — safe context budget per file
                var allChunksInFile = chunks
                    .Where(c => c.FilePath == result.Chunk.FilePath)
                    .OrderBy(c => c.StartLine)
                    .ToList();

                var fullFileContent = string.Join("\n", allChunksInFile.Select(c => c.Content));
                var fileStart = allChunksInFile.First().StartLine;
                var fileEnd = allChunksInFile.Last().EndLine;

                string displayContent;
                string locationLabel;

                if (fullFileContent.Length <= MAX_FULL_FILE_CHARS)
                {
                    // Small enough — send the whole file for full context
                    displayContent = fullFileContent;
                    locationLabel = $"{result.Chunk.FilePath}:L{fileStart}-L{fileEnd} (full file, {fullFileContent.Length} chars, ~{fullFileContent.Length / 4} tokens)";
                    _logger.LogInformation("   📄 Sending FULL FILE ({Length} chars, {Chunks} chunks)", fullFileContent.Length, allChunksInFile.Count);
                }
                else
                {
                    // File too large — fall back to matched chunk ± 1 neighbor window
                    var matchedIdx = result.Chunk.ChunkIndex;
                    var windowChunks = allChunksInFile
                        .Where(c => c.ChunkIndex >= matchedIdx - 1 && c.ChunkIndex <= matchedIdx + 1)
                        .OrderBy(c => c.StartLine)
                        .ToList();

                    displayContent = string.Join("\n", windowChunks.Select(c => c.Content));
                    var windowStart = windowChunks.First().StartLine;
                    var windowEnd = windowChunks.Last().EndLine;
                    locationLabel = $"{result.Chunk.FilePath}:L{windowStart}-L{windowEnd} (window, {displayContent.Length} chars, ~{displayContent.Length / 4} tokens)";
                    _logger.LogInformation("   📄 File too large ({Length} chars) — sending window around matched chunk", fullFileContent.Length);
                }

                context.AppendLine($"### Similar code (relevance: {result.Similarity:F2})");
                context.AppendLine($"Location: {locationLabel}");
                context.AppendLine("```");
                context.AppendLine(displayContent);
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
    /// Build 2-level dependency graph context from the in-memory chunk store (no ADO calls).
    /// Supports TypeScript/JS relative imports, C#, Python, Rust.
    /// </summary>
    public Task<string> BuildDependencyGraphContextAsync(
        PullRequestFile file,
        string repositoryId)
    {
        if (!_inMemoryStore.TryGetValue(repositoryId, out var allChunks) || allChunks.Count == 0)
            return Task.FromResult(string.Empty);

        // Build lookup: normalized path → chunks for that file
        var chunksByPath = allChunks
            .GroupBy(c => NormalizeDepPath(c.FilePath))
            .ToDictionary(g => g.Key, g => g.ToList());

        const int MAX_DEP_FILES = 8;
        const int CHAR_BUDGET = 8000;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new StringBuilder();
        result.AppendLine("## Dependency Context (import graph)");
        int totalChars = 0;
        int fetched = 0;

        // Level 1: direct imports of the changed file
        var level1Paths = ResolveImports(file.Content, file.Path, chunksByPath);
        _logger.LogInformation("🔗 Dep graph L1: {Count} imports from {File}", level1Paths.Count, file.Path);

        var queue = new Queue<(string path, int level)>();
        foreach (var p in level1Paths)
            queue.Enqueue((p, 1));

        while (queue.Count > 0 && fetched < MAX_DEP_FILES && totalChars < CHAR_BUDGET)
        {
            var (depPath, level) = queue.Dequeue();
            if (!seen.Add(depPath)) continue;

            if (!chunksByPath.TryGetValue(depPath, out var depChunks)) continue;

            var depContent = string.Join("\n", depChunks.OrderBy(c => c.StartLine).Select(c => c.Content));
            var declarations = ExtractDeclarations(depContent, depChunks[0].FilePath);
            if (string.IsNullOrWhiteSpace(declarations)) continue;

            var remaining = CHAR_BUDGET - totalChars;
            if (declarations.Length > remaining)
                declarations = declarations.Substring(0, remaining) + "\n// [truncated]";

            result.AppendLine($"### {depChunks[0].FilePath} (L{level} dep)");
            result.AppendLine("```");
            result.AppendLine(declarations);
            result.AppendLine("```");
            result.AppendLine();

            totalChars += declarations.Length;
            fetched++;

            // Enqueue level-2 imports
            if (level == 1)
            {
                var level2Paths = ResolveImports(depContent, depChunks[0].FilePath, chunksByPath);
                foreach (var p2 in level2Paths.Where(p => !seen.Contains(p)))
                    queue.Enqueue((p2, 2));
            }
        }

        if (fetched == 0) return Task.FromResult(string.Empty);

        _logger.LogInformation("🔗 Dependency graph: {Count} files, {Chars} chars", fetched, totalChars);
        return Task.FromResult(result.ToString());
    }

    /// <summary>
    /// Resolve imports/using statements to normalized chunk paths present in the in-memory store.
    /// </summary>
    private List<string> ResolveImports(string? content, string sourceFilePath, Dictionary<string, List<CodeChunk>> chunksByPath)
    {
        if (string.IsNullOrEmpty(content)) return new List<string>();

        var ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        var sourceDir = (Path.GetDirectoryName(sourceFilePath.Replace('\\', '/')) ?? string.Empty).TrimStart('/');
        var rawImports = new List<string>();

        if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mts" or ".cts")
        {
            // import ... from './foo'  |  export ... from './foo'
            var fromMatches = Regex.Matches(content,
                @"(?:import|export)\s+(?:[\w\s{},*]+\s+from\s+)?[""'`](\.\.?/[^""'`\n]+)[""'`]");
            foreach (Match m in fromMatches)
                rawImports.Add(m.Groups[1].Value);

            // require('./foo')  |  import('./foo')
            var requireMatches = Regex.Matches(content,
                @"(?:require|import)\s*\(\s*[""'`](\.\.?/[^""'`\n]+)[""'`]\s*\)");
            foreach (Match m in requireMatches)
                rawImports.Add(m.Groups[1].Value);
        }
        else if (ext == ".cs")
        {
            // Match using statements and search by type name in indexed filenames
            var usingMatches = Regex.Matches(content, @"using\s+([A-Za-z0-9_.]+);");
            var resolved = new List<string>();
            foreach (Match m in usingMatches)
            {
                var typeName = m.Groups[1].Value.Split('.').Last();
                var found = chunksByPath.Keys.FirstOrDefault(k =>
                    string.Equals(Path.GetFileNameWithoutExtension(k), typeName, StringComparison.OrdinalIgnoreCase));
                if (found != null) resolved.Add(found);
            }
            return resolved.Distinct().Take(6).ToList();
        }
        else if (ext == ".py")
        {
            // from .module import X  |  from module import X
            var fromMatches = Regex.Matches(content, @"from\s+(\.?[A-Za-z0-9_.]+)\s+import");
            foreach (Match m in fromMatches)
            {
                var mod = m.Groups[1].Value.TrimStart('.');
                rawImports.Add(mod.StartsWith('.') ? "./" + mod.Replace('.', '/') : "/" + mod.Replace('.', '/'));
            }
        }
        else if (ext == ".rs")
        {
            var useMatches = Regex.Matches(content, @"use\s+(?:crate::)?([A-Za-z0-9_:]+)");
            foreach (Match m in useMatches)
            {
                var segs = m.Groups[1].Value
                    .Split("::", StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => !s.Equals("self", StringComparison.OrdinalIgnoreCase)
                             && !s.Equals("super", StringComparison.OrdinalIgnoreCase)
                             && !s.Equals("crate", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (segs.Count > 0 && char.IsUpper(segs[^1][0])) segs.RemoveAt(segs.Count - 1);
                if (segs.Count > 0) rawImports.Add("/src/" + string.Join("/", segs));
            }
        }

        // Resolve relative paths and find matching keys in chunk store
        var tsExtensions = new[] { ".ts", ".tsx", ".js", ".jsx", ".mts", ".cts" };
        var resolved2 = new List<string>();
        foreach (var raw in rawImports.Distinct())
        {
            string basePath = raw.StartsWith('/')
                ? raw.TrimStart('/')
                : ResolveRelativePath(sourceDir, raw);

            basePath = NormalizeDepPath(basePath);
            // Strip query strings / hash fragments
            var qIdx = basePath.IndexOfAny(new[] { '?', '#' });
            if (qIdx >= 0) basePath = basePath.Substring(0, qIdx);

            // Try exact match
            if (chunksByPath.ContainsKey(basePath)) { resolved2.Add(basePath); continue; }

            // Try adding extensions
            bool found = false;
            foreach (var tryExt in tsExtensions)
            {
                var candidate = NormalizeDepPath(basePath + tryExt);
                if (chunksByPath.ContainsKey(candidate)) { resolved2.Add(candidate); found = true; break; }
            }
            if (found) continue;

            // Try /index.* barrel files
            foreach (var tryExt in tsExtensions)
            {
                var candidate = NormalizeDepPath(basePath + "/index" + tryExt);
                if (chunksByPath.ContainsKey(candidate)) { resolved2.Add(candidate); break; }
            }
        }

        return resolved2.Distinct().Take(6).ToList();
    }

    private string ResolveRelativePath(string sourceDir, string relativePath)
    {
        var combined = string.IsNullOrEmpty(sourceDir) ? relativePath : sourceDir + "/" + relativePath;
        var parts = combined.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var part in parts)
        {
            if (part == "..") { if (stack.Count > 0) stack.Pop(); }
            else if (part != ".") stack.Push(part);
        }
        return string.Join("/", stack.Reverse());
    }

    private static string NormalizeDepPath(string path) =>
        path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    /// <summary>
    /// Extract only exported/public declarations from file content (not full file body).
    /// </summary>
    private string ExtractDeclarations(string content, string filePath)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var lines = content.Split('\n');
        var kept = new List<string>();

        if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mts" or ".cts")
        {
            int braceDepth = 0;
            bool capturing = false;
            foreach (var line in lines)
            {
                var t = line.TrimStart();
                bool isDecl = t.StartsWith("export ") || t.StartsWith("interface ") ||
                              t.StartsWith("type ") || t.StartsWith("abstract class") ||
                              t.StartsWith("declare ") || t.StartsWith("class ") ||
                              t.StartsWith("enum ") || t.StartsWith("/** ") ||
                              t.StartsWith(" * ") || t.StartsWith("*/");

                if (isDecl || capturing)
                {
                    kept.Add(line);
                    braceDepth += line.Count(c => c == '{') - line.Count(c => c == '}');
                    capturing = braceDepth > 0;
                }
            }
        }
        else if (ext == ".cs")
        {
            foreach (var line in lines)
            {
                var t = line.TrimStart();
                if (Regex.IsMatch(t, @"^(public|internal|protected)\s") &&
                    (t.Contains("class ") || t.Contains("interface ") || t.Contains("record ") ||
                     t.Contains("enum ") || t.Contains("(") || t.Contains("=>")))
                    kept.Add(line);
            }
        }
        else if (ext == ".py")
        {
            foreach (var line in lines)
            {
                var t = line.TrimStart();
                if (t.StartsWith("class ") || t.StartsWith("def ") || t.StartsWith("async def ") ||
                    t.StartsWith("@") || t.StartsWith("\"\"\""))
                    kept.Add(line);
            }
        }
        else if (ext == ".rs")
        {
            foreach (var line in lines)
            {
                var t = line.TrimStart();
                if (t.StartsWith("pub ") || t.StartsWith("pub(") || t.StartsWith("struct ") ||
                    t.StartsWith("trait ") || t.StartsWith("impl ") || t.StartsWith("fn ") ||
                    t.StartsWith("type ") || t.StartsWith("enum ") || t.StartsWith("///"))
                    kept.Add(line);
            }
        }
        else
        {
            return string.Join("\n", lines.Take(30));
        }

        return kept.Count > 0 ? string.Join("\n", kept) : string.Join("\n", lines.Take(30));
    }

    /// <summary>
    /// Caller search: find call sites in the indexed repo for functions modified in this file's diff.
    /// Catches breaking API changes — shows which files call functions that were renamed/changed.
    /// Pure in-memory scan, no embeddings needed.
    /// </summary>
    public string GetCallerContext(PullRequestFile file, string repositoryId)
    {
        if (!_inMemoryStore.TryGetValue(repositoryId, out var allChunks) || allChunks.Count == 0)
            return string.Empty;

        var modifiedFunctions = ExtractModifiedFunctionNames(file.UnifiedDiff, file.Path);
        if (modifiedFunctions.Count == 0) return string.Empty;

        _logger.LogInformation("🔍 Caller search: {Count} modified functions [{Names}]",
            modifiedFunctions.Count, string.Join(", ", modifiedFunctions));

        var sourceNormalized = NormalizeDepPath(file.Path);
        const int MAX_CALLERS = 5;
        const int CHAR_BUDGET = 4000;

        var result = new StringBuilder();
        result.AppendLine("## Caller Context (who calls modified functions)");

        var seenFilePerFn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalChars = 0;
        int callerCount = 0;

        foreach (var fnName in modifiedFunctions)
        {
            if (callerCount >= MAX_CALLERS || totalChars >= CHAR_BUDGET) break;

            foreach (var chunk in allChunks)
            {
                if (callerCount >= MAX_CALLERS || totalChars >= CHAR_BUDGET) break;
                if (NormalizeDepPath(chunk.FilePath) == sourceNormalized) continue;

                var key = $"{chunk.FilePath}|{fnName}";
                if (seenFilePerFn.Contains(key)) continue;

                if (!chunk.Content.Contains(fnName + "(", StringComparison.Ordinal) &&
                    !chunk.Content.Contains(fnName + " (", StringComparison.Ordinal))
                    continue;

                seenFilePerFn.Add(key);

                var snippet = ExtractCallSiteSnippet(chunk.Content, fnName, contextLines: 3);
                if (string.IsNullOrWhiteSpace(snippet)) continue;

                var remaining = CHAR_BUDGET - totalChars;
                if (snippet.Length > remaining)
                    snippet = snippet.Substring(0, remaining) + "\n// [truncated]";

                result.AppendLine($"### {chunk.FilePath} (calls `{fnName}`)");
                result.AppendLine("```");
                result.AppendLine(snippet);
                result.AppendLine("```");
                result.AppendLine();

                totalChars += snippet.Length;
                callerCount++;
            }
        }

        if (callerCount == 0) return string.Empty;

        _logger.LogInformation("🔍 Caller search: {Count} callers found, {Chars} chars", callerCount, totalChars);
        return result.ToString();
    }

    private List<string> ExtractModifiedFunctionNames(string? unifiedDiff, string filePath) =>
        ExtractFunctionNamesFromDiffLines(unifiedDiff, filePath, addedLines: true);

    private List<string> ExtractFunctionNamesFromDiffLines(string? unifiedDiff, string filePath, bool addedLines)
    {
        if (string.IsNullOrEmpty(unifiedDiff)) return new List<string>();

        var ext    = Path.GetExtension(filePath).ToLowerInvariant();
        var names  = new List<string>();
        var prefix = addedLines ? '+' : '-';
        var skipPrefix = addedLines ? "+++" : "---";

        var lines = unifiedDiff
            .Split('\n')
            .Where(l => l.Length > 0 && l[0] == prefix && !l.StartsWith(skipPrefix))
            .Select(l => l.Substring(1).TrimStart());

        foreach (var line in lines)
        {
            string? name = null;

            if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mts" or ".cts")
            {
                var m = Regex.Match(line, @"(?:export\s+)?(?:async\s+)?function\s+(\w+)\s*[(<]");
                if (m.Success) name = m.Groups[1].Value;
                if (name == null)
                {
                    m = Regex.Match(line, @"(?:export\s+)?(?:const|let)\s+(\w+)\s*=\s*(?:async\s*)?\(");
                    if (m.Success) name = m.Groups[1].Value;
                }
                if (name == null)
                {
                    m = Regex.Match(line, @"^\s*(?:public|private|protected|static|async|override)[\s\w]*\s+(\w+)\s*\(");
                    if (m.Success && !IsKeyword(m.Groups[1].Value)) name = m.Groups[1].Value;
                }
            }
            else if (ext == ".cs")
            {
                var m = Regex.Match(line,
                    @"(?:public|private|protected|internal|static|async|virtual|override|abstract)\s+(?:[\w<>\[\]?,\s]+\s+)+(\w+)\s*\(");
                if (m.Success && !IsKeyword(m.Groups[1].Value)) name = m.Groups[1].Value;
            }
            else if (ext == ".py")
            {
                var m = Regex.Match(line, @"(?:async\s+)?def\s+(\w+)\s*\(");
                if (m.Success) name = m.Groups[1].Value;
            }
            else if (ext == ".rs")
            {
                var m = Regex.Match(line, @"(?:pub\s+)?(?:async\s+)?fn\s+(\w+)\s*[(<]");
                if (m.Success) name = m.Groups[1].Value;
            }

            if (name != null && name.Length > 2 && !IsKeyword(name) && !names.Contains(name))
                names.Add(name);
        }

        return names.Take(8).ToList();
    }

    private (List<string> SignatureChanged, List<string> Deleted) ExtractSignatureChangedFunctions(
        string? unifiedDiff, string filePath)
    {
        if (string.IsNullOrEmpty(unifiedDiff)) return (new(), new());
        var added   = ExtractFunctionNamesFromDiffLines(unifiedDiff, filePath, addedLines: true);
        var removed = ExtractFunctionNamesFromDiffLines(unifiedDiff, filePath, addedLines: false);
        var changed = removed.Intersect(added, StringComparer.Ordinal).ToList();
        var deleted = removed.Except(added, StringComparer.Ordinal).ToList();
        return (changed, deleted);
    }

    public string GetSignatureChangeImpactContext(PullRequestFile file, string repositoryId)
    {
        if (!_inMemoryStore.TryGetValue(repositoryId, out var allChunks) || allChunks.Count == 0)
            return string.Empty;

        var (signatureChanged, deleted) = ExtractSignatureChangedFunctions(file.UnifiedDiff, file.Path);
        var impactedFunctions = signatureChanged.Concat(deleted).Distinct().ToList();
        if (impactedFunctions.Count == 0) return string.Empty;

        _logger.LogInformation("⚠️ Impact scan: {Sig} signature-changed, {Del} deleted functions [{Names}]",
            signatureChanged.Count, deleted.Count, string.Join(", ", impactedFunctions));

        var sourceNormalized = NormalizeDepPath(file.Path);
        const int MAX_CALLERS = 6;
        const int CHAR_BUDGET = 4000;

        var result = new StringBuilder();
        result.AppendLine("## ⚠️ Cross-File Impact Warning");
        result.AppendLine("The following functions had their **signatures changed or were deleted**.");
        result.AppendLine("Each call site below may need to be updated to match the new signature.");
        result.AppendLine();

        if (signatureChanged.Count > 0)
            result.AppendLine($"**Signature changed:** `{string.Join("`, `", signatureChanged)}`");
        if (deleted.Count > 0)
            result.AppendLine($"**Deleted:** `{string.Join("`, `", deleted)}`");
        result.AppendLine();

        var seenKey    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalChars = 0;
        int callerCount = 0;

        foreach (var fnName in impactedFunctions)
        {
            if (callerCount >= MAX_CALLERS || totalChars >= CHAR_BUDGET) break;
            foreach (var chunk in allChunks)
            {
                if (callerCount >= MAX_CALLERS || totalChars >= CHAR_BUDGET) break;
                if (NormalizeDepPath(chunk.FilePath) == sourceNormalized) continue;
                var key = $"{chunk.FilePath}|{fnName}";
                if (seenKey.Contains(key)) continue;
                if (!chunk.Content.Contains(fnName + "(", StringComparison.Ordinal) &&
                    !chunk.Content.Contains(fnName + " (", StringComparison.Ordinal)) continue;
                seenKey.Add(key);
                var snippet = ExtractCallSiteSnippet(chunk.Content, fnName, contextLines: 3);
                if (string.IsNullOrWhiteSpace(snippet)) continue;
                var remaining = CHAR_BUDGET - totalChars;
                if (snippet.Length > remaining) snippet = snippet[..remaining] + "\n// [truncated]";
                var label = deleted.Contains(fnName) ? "calls deleted" : "calls changed";
                result.AppendLine($"### {chunk.FilePath} ({label} `{fnName}`)");
                result.AppendLine("```");
                result.AppendLine(snippet);
                result.AppendLine("```");
                result.AppendLine();
                totalChars += snippet.Length;
                callerCount++;
            }
        }

        if (callerCount == 0) return string.Empty;

        _logger.LogInformation("⚠️ Impact scan: {Count} call sites found", callerCount);
        return result.ToString();
    }

    private static bool IsKeyword(string name) =>
        name is "if" or "else" or "for" or "while" or "do" or "switch" or "case" or "return"
              or "new" or "const" or "let" or "var" or "class" or "interface" or "type" or "enum"
              or "import" or "export" or "async" or "await" or "void" or "null" or "true" or "false"
              or "this" or "super" or "static" or "public" or "private" or "protected" or "override"
              or "get" or "set" or "constructor" or "extends" or "implements";

    private static string ExtractCallSiteSnippet(string content, string fnName, int contextLines)
    {
        var lines = content.Split('\n');
        var result = new List<string>();
        var p1 = fnName + "(";
        var p2 = fnName + " (";

        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(p1, StringComparison.Ordinal) &&
                !lines[i].Contains(p2, StringComparison.Ordinal)) continue;

            var start = Math.Max(0, i - contextLines);
            var end = Math.Min(lines.Length - 1, i + contextLines);
            if (result.Count > 0) result.Add("// ...");
            result.AddRange(lines[start..(end + 1)]);
            i = end;
        }

        return string.Join("\n", result.Take(40));
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

        // 3. Dependency context (related files) — 2-level import graph from in-memory store
        _logger.LogInformation("Step 2: Searching for dependency context...");
        var depStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var depContext = await BuildDependencyGraphContextAsync(file, repositoryId);
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

        // 4. Caller search — who calls the functions modified in this diff
        _logger.LogInformation("Step 3: Searching for callers of modified functions...");
        var callerStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var callerContext = GetCallerContext(file, repositoryId);
        callerStopwatch.Stop();

        if (!string.IsNullOrEmpty(callerContext))
        {
            context.AppendLine(callerContext);
            _logger.LogInformation("✅ Caller Context Added in {ElapsedMs}ms: {Length} chars",
                callerStopwatch.ElapsedMilliseconds, callerContext.Length);
        }
        else
        {
            _logger.LogInformation("❌ No caller context found ({ElapsedMs}ms)", callerStopwatch.ElapsedMilliseconds);
        }

        // 5. Signature-change cross-file impact
        _logger.LogInformation("Step 4: Scanning for cross-file signature-change impact...");
        var impactStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var impactContext = GetSignatureChangeImpactContext(file, repositoryId);
        impactStopwatch.Stop();

        if (!string.IsNullOrEmpty(impactContext))
        {
            context.AppendLine(impactContext);
            _logger.LogInformation("⚠️ Signature Impact Context Added in {ElapsedMs}ms: {Length} chars",
                impactStopwatch.ElapsedMilliseconds, impactContext.Length);
        }
        else
        {
            _logger.LogInformation("✅ No signature-change impact detected ({ElapsedMs}ms)", impactStopwatch.ElapsedMilliseconds);
        }

        // 6. Test context (test files likely covering the target file)
        _logger.LogInformation("Step 5: Searching for related test context...");
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
        _logger.LogInformation("   Caller search: {CallerMs}ms", callerStopwatch?.ElapsedMilliseconds ?? 0);
        _logger.LogInformation("   Impact scan: {ImpactMs}ms", impactStopwatch?.ElapsedMilliseconds ?? 0);
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
    private string BuildAuthenticatedCloneUrl(string originalUrl, string? accessTokenOverride = null)
    {
        var preferredToken = NormalizeAdoAccessToken(accessTokenOverride);
        var fallbackPat = _adoClient.PersonalAccessToken;

        if (string.IsNullOrEmpty(preferredToken) && string.IsNullOrEmpty(fallbackPat))
        {
            return originalUrl; // Return original URL if no credentials are available
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
                    var credentialToken = !string.IsNullOrWhiteSpace(preferredToken) ? preferredToken : fallbackPat;
                    var credentialSource = !string.IsNullOrWhiteSpace(preferredToken) ? "X-Ado-Access-Token" : "ADO_PAT";
                    
                    // Use visualstudio.com format with username and DefaultCollection
                    var authenticatedUrl = $"https://git:{credentialToken}@{organization.ToLower()}.visualstudio.com/DefaultCollection/{project}/{repoPath}";
                    _logger.LogInformation("🔐 Using {CredentialSource} as primary clone credential for Azure DevOps", credentialSource);
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

    private static string? NormalizeAdoAccessToken(string? accessTokenOverride)
    {
        if (string.IsNullOrWhiteSpace(accessTokenOverride))
        {
            return null;
        }

        var token = accessTokenOverride.Trim();
        const string bearerPrefix = "Bearer ";
        if (token.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            token = token[bearerPrefix.Length..].Trim();
        }

        return string.IsNullOrWhiteSpace(token) ? null : token;
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

    private async Task<(bool Success, string Output, string Error)> RunGitCommandAsync(string arguments, string workingDirectory, int timeoutSeconds = 180)
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

                    return (false, string.Empty, $"Git command timed out after {timeoutSeconds} seconds");
                }

                // Process completed within timeout
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                if (process.ExitCode != 0)
                {
                    _logger.LogError("❌ Git command failed with exit code {ExitCode} after {ElapsedMs}ms",
                        process.ExitCode, stopwatch.ElapsedMilliseconds);
                    _logger.LogError("Error output: {Error}", error);
                    return (false, string.Empty, error);
                }

                _logger.LogInformation("✅ Git command completed successfully in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
                if (!string.IsNullOrEmpty(output))
                {
                    _logger.LogDebug("Command output: {Output}", output);
                }

                return (true, output, string.Empty);
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

                return (false, string.Empty, $"Git command timed out after {timeoutSeconds} seconds");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Exception running git command: git {Arguments}", RedactSecrets(arguments));
            return (false, string.Empty, ex.Message);
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
                        // Prepend language tag so embeddings are language-aware
                        var langTag = GetLanguageTag(chunk.FilePath);
                        var textToEmbed = string.IsNullOrEmpty(langTag) ? chunk.Content : $"{langTag}\n{chunk.Content}";
                        var embeddingResponse = await GenerateEmbeddingWithRetryAsync(textToEmbed);
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

    private static string GetLanguageTag(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".ts" or ".tsx" => "[TypeScript]",
            ".js" or ".jsx" => "[JavaScript]",
            ".cs" => "[CSharp]",
            ".rs" => "[Rust]",
            ".java" => "[Java]",
            ".py" => "[Python]",
            ".go" => "[Go]",
            ".cpp" or ".cc" or ".cxx" => "[C++]",
            ".c" => "[C]",
            ".rb" => "[Ruby]",
            ".swift" => "[Swift]",
            ".kt" => "[Kotlin]",
            ".sh" or ".bash" => "[Shell]",
            ".bicep" => "[Bicep]",
            ".json" => "[JSON]",
            ".yaml" or ".yml" => "[YAML]",
            _ => string.Empty
        };
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

        // Prepend language tag so the query vector is in the same language-aware space as indexed chunks
        var langTag = GetLanguageTag(file.Path);
        if (!string.IsNullOrEmpty(langTag))
        {
            queryParts.Add(langTag);
            _logger.LogDebug("   Added language tag: {LangTag}", langTag);
        }

        // Extract meaningful content from changes
        if (!string.IsNullOrEmpty(file.UnifiedDiff))
        {
            _logger.LogDebug("   Processing unified diff ({Length} chars)...", file.UnifiedDiff.Length);

            var allDiffLines = file.UnifiedDiff.Split('\n');

            // Added lines carry intent; take up to 30 for richer signal
            var addedLines = allDiffLines
                .Where(l => l.StartsWith('+') && !l.StartsWith("+++"))
                .Select(l => l.Substring(1).Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length > 5)
                .Take(30)
                .ToList();

            // Context lines (unchanged, starting with space) give surrounding semantic context
            var contextLines = allDiffLines
                .Where(l => l.Length > 0 && l[0] == ' ')
                .Select(l => l.Substring(1).Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length > 5)
                .Take(10)
                .ToList();

            _logger.LogDebug("   Added lines selected: {Added}, context lines: {Ctx}", addedLines.Count, contextLines.Count);

            queryParts.AddRange(addedLines);
            queryParts.AddRange(contextLines);
        }

        // Add file name and directory for path-level signal
        var fileName = Path.GetFileNameWithoutExtension(file.Path);
        var dirName = Path.GetDirectoryName(file.Path)?.Replace('\\', '/') ?? string.Empty;
        queryParts.Add($"file {fileName}");
        if (!string.IsNullOrEmpty(dirName))
            queryParts.Add($"directory {dirName}");

        _logger.LogDebug("   Added file/dir context: '{FileName}', '{DirName}'", fileName, dirName);

        var query = string.Join(' ', queryParts);
        var finalQuery = query.Length > 2000 ? query.Substring(0, 2000) : query;

        _logger.LogDebug("   Final query length: {Length} chars (max 2000)", finalQuery.Length);
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

    // ParseDependencies and GetFileSummary removed — replaced by BuildDependencyGraphContextAsync,
    // ResolveImports, and ExtractDeclarations (2-level import graph with proper TS/JS relative resolution).

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

        if (vector is Memory<float> floatMemory)
        {
            return floatMemory.ToArray();
        }

        if (vector is ReadOnlyMemory<float> readOnlyFloatMemory)
        {
            return readOnlyFloatMemory.ToArray();
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

    private static float[] ExtractEmbeddingVector(object embeddingResult)
    {
        try
        {
            var vector = ((dynamic)embeddingResult).Vector;
            var asArray = ToFloatVector(vector);
            if (asArray.Length > 0)
            {
                return asArray;
            }
        }
        catch
        {
            // Fall through to additional extraction attempts.
        }

        try
        {
            return ToFloatVector(embeddingResult);
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
