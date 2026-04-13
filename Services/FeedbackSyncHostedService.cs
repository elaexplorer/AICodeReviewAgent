using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// Background service that periodically sweeps all known PRs and syncs their ADO thread
/// resolution statuses (fixed / wontFix / byDesign / active) into the feedback SQLite DB.
///
/// Interval: default 6 hours, overridable via FEEDBACK_SYNC_INTERVAL_MINUTES env var.
/// </summary>
public class FeedbackSyncHostedService : BackgroundService
{
    private readonly CommentFeedbackService _feedbackService;
    private readonly AzureDevOpsMcpClient   _adoClient;
    private readonly AdoConfigurationService _adoConfig;
    private readonly ILogger<FeedbackSyncHostedService> _logger;

    public FeedbackSyncHostedService(
        CommentFeedbackService feedbackService,
        AzureDevOpsMcpClient adoClient,
        AdoConfigurationService adoConfig,
        ILogger<FeedbackSyncHostedService> logger)
    {
        _feedbackService = feedbackService;
        _adoClient       = adoClient;
        _adoConfig       = adoConfig;
        _logger          = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = int.TryParse(
            Environment.GetEnvironmentVariable("FEEDBACK_SYNC_INTERVAL_MINUTES"), out var m) && m > 0
            ? m : 360; // default 6 hours

        _logger.LogInformation("FeedbackSync: interval = {Minutes} min", intervalMinutes);

        // Initial delay — let the rest of startup finish first
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
        bool firstRun = true;
        do
        {
            if (!firstRun)
                _logger.LogInformation("FeedbackSync: periodic sync triggered");

            await SyncAllAsync(stoppingToken);
            firstRun = false;
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncAllAsync(CancellationToken stoppingToken)
    {
        // PRs with threads that have never been synced or are still active/pending
        var prs = _feedbackService.GetPrsNeedingSync(staleAfter: TimeSpan.FromHours(6));

        if (prs.Count == 0)
        {
            _logger.LogInformation("FeedbackSync: nothing to sync");
            return;
        }

        _logger.LogInformation("FeedbackSync: syncing {Count} PR(s)", prs.Count);
        var totalSynced = 0;

        foreach (var (project, repository, prId) in prs)
        {
            if (stoppingToken.IsCancellationRequested) break;
            try
            {
                var repoInfo = await _adoClient.GetRepositoryAsync(project, repository);
                if (repoInfo?.Id is null)
                {
                    _logger.LogWarning("FeedbackSync: repo not found {Project}/{Repository}", project, repository);
                    continue;
                }

                var synced = await _feedbackService.SyncThreadStatusesForPrAsync(
                    project, repository, prId,
                    async (p, r, id) => await _adoClient.GetAgentThreadStatusesAsync(p, repoInfo.Id, id));

                if (synced > 0)
                {
                    _logger.LogInformation(
                        "FeedbackSync: PR {PrId} ({Repository}) — {Synced} thread(s) updated",
                        prId, repository, synced);
                    totalSynced += synced;
                }

                // Small delay between PRs to avoid hammering ADO
                await Task.Delay(500, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FeedbackSync: failed to sync PR {PrId}", prId);
            }
        }

        _logger.LogInformation("FeedbackSync: cycle complete — {Total} thread(s) synced across {Prs} PR(s)",
            totalSynced, prs.Count);
    }
}
