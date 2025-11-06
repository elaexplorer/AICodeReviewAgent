using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// In-memory cache for codebase files and structure
/// </summary>
public class CodebaseCache
{
    private readonly ILogger<CodebaseCache> _logger;
    private readonly ConcurrentDictionary<string, CachedFile> _fileCache;
    private readonly ConcurrentDictionary<string, RepositoryStructure> _repositoryStructure;

    public CodebaseCache(ILogger<CodebaseCache> logger)
    {
        _logger = logger;
        _fileCache = new ConcurrentDictionary<string, CachedFile>();
        _repositoryStructure = new ConcurrentDictionary<string, RepositoryStructure>();
    }

    public void CacheFile(string repositoryId, string filePath, string content, string version)
    {
        var key = GetCacheKey(repositoryId, filePath, version);
        _fileCache[key] = new CachedFile
        {
            RepositoryId = repositoryId,
            FilePath = filePath,
            Content = content,
            Version = version,
            CachedAt = DateTime.UtcNow
        };
        _logger.LogDebug("Cached file {FilePath} from repository {RepositoryId} at version {Version}",
            filePath, repositoryId, version);
    }

    public string? GetCachedFile(string repositoryId, string filePath, string version)
    {
        var key = GetCacheKey(repositoryId, filePath, version);
        if (_fileCache.TryGetValue(key, out var cached))
        {
            _logger.LogDebug("Cache hit for file {FilePath} from repository {RepositoryId} at version {Version}",
                filePath, repositoryId, version);
            return cached.Content;
        }
        return null;
    }

    public void CacheRepositoryStructure(string repositoryId, string branch, List<string> files)
    {
        var key = $"{repositoryId}:{branch}";
        _repositoryStructure[key] = new RepositoryStructure
        {
            RepositoryId = repositoryId,
            Branch = branch,
            Files = files,
            CachedAt = DateTime.UtcNow
        };
        _logger.LogInformation("Cached repository structure for {RepositoryId} on branch {Branch} with {FileCount} files",
            repositoryId, branch, files.Count);
    }

    public List<string>? GetCachedRepositoryStructure(string repositoryId, string branch)
    {
        var key = $"{repositoryId}:{branch}";
        if (_repositoryStructure.TryGetValue(key, out var structure))
        {
            _logger.LogDebug("Cache hit for repository structure {RepositoryId} on branch {Branch}",
                repositoryId, branch);
            return structure.Files;
        }
        return null;
    }

    public void ClearCache()
    {
        _fileCache.Clear();
        _repositoryStructure.Clear();
        _logger.LogInformation("Cleared all caches");
    }

    private static string GetCacheKey(string repositoryId, string filePath, string version)
    {
        return $"{repositoryId}:{filePath}:{version}";
    }

    private class CachedFile
    {
        public string RepositoryId { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public DateTime CachedAt { get; set; }
    }

    private class RepositoryStructure
    {
        public string RepositoryId { get; set; } = string.Empty;
        public string Branch { get; set; } = string.Empty;
        public List<string> Files { get; set; } = new();
        public DateTime CachedAt { get; set; }
    }
}
