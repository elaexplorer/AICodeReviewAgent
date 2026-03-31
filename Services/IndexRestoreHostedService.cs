using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// On startup, reloads any previously-persisted embeddings into the in-memory store
/// so that the agent doesn't require re-indexing after a restart.
/// </summary>
public class IndexRestoreHostedService : IHostedService
{
    private readonly EmbeddingPersistenceService _persistence;
    private readonly CodebaseContextService _contextService;
    private readonly ILogger<IndexRestoreHostedService> _logger;

    public IndexRestoreHostedService(
        EmbeddingPersistenceService persistence,
        CodebaseContextService contextService,
        ILogger<IndexRestoreHostedService> logger)
    {
        _persistence = persistence;
        _contextService = contextService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var repos = await _persistence.GetIndexedRepositoriesAsync();
        if (repos.Count == 0)
        {
            _logger.LogInformation("📦 No persisted embeddings found — starting fresh.");
            return;
        }

        _logger.LogInformation("📦 Restoring embeddings for {Count} repository(ies) from SQLite…", repos.Count);
        foreach (var repoId in repos)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await _contextService.RestoreIndexFromDiskAsync(repoId);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
