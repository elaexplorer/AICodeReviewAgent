using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// Restores the pipeline token from Azure Blob Storage on container startup,
/// so the delete-comments endpoint works immediately after a revision change.
/// </summary>
public sealed class PipelineTokenRestoreHostedService : IHostedService
{
    private readonly PipelineTokenStore _store;
    private readonly ILogger<PipelineTokenRestoreHostedService> _logger;

    public PipelineTokenRestoreHostedService(
        PipelineTokenStore store,
        ILogger<PipelineTokenRestoreHostedService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Restoring pipeline token from blob storage...");
        await _store.RestoreFromBlobAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
