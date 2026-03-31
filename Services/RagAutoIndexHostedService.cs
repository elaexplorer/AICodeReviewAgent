using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// On startup, auto-indexes any repos listed in the RAG_AUTO_INDEX_REPOS environment variable
/// so RAG context is available immediately after a container restart without a manual API call.
///
/// Format: comma-separated "Project/Repository:branch" entries.
/// Example: RAG_AUTO_INDEX_REPOS=SCC/service-shared_framework_waimea:main,SCC/other-repo:master
/// </summary>
public class RagAutoIndexHostedService : BackgroundService
{
    private readonly CodebaseContextService _codebaseContextService;
    private readonly AdoConfigurationService _adoConfig;
    private readonly ILogger<RagAutoIndexHostedService> _logger;
    private readonly List<AutoIndexTarget> _targets;

    public RagAutoIndexHostedService(
        CodebaseContextService codebaseContextService,
        AdoConfigurationService adoConfig,
        ILogger<RagAutoIndexHostedService> logger)
    {
        _codebaseContextService = codebaseContextService;
        _adoConfig = adoConfig;
        _logger = logger;
        _targets = ParseTargets(Environment.GetEnvironmentVariable("RAG_AUTO_INDEX_REPOS"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_targets.Count == 0)
        {
            _logger.LogInformation("RAG auto-index: RAG_AUTO_INDEX_REPOS not set — skipping auto-index on startup.");
            return;
        }

        // Small delay so the rest of startup (IndexRestoreHostedService, DI, etc.) completes first
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        // Write git credentials so clone can authenticate in a headless container (no TTY)
        ConfigureGitCredentials(_adoConfig.PersonalAccessToken);

        _logger.LogInformation("RAG auto-index: starting background indexing for {Count} repo(s)", _targets.Count);

        foreach (var target in _targets)
        {
            if (stoppingToken.IsCancellationRequested) break;

            if (_codebaseContextService.IsRepositoryIndexed(target.Repository))
            {
                _logger.LogInformation(
                    "RAG auto-index: {Repository} already indexed ({Chunks} chunks) — skipping",
                    target.Repository,
                    _codebaseContextService.GetChunkCount(target.Repository));
                continue;
            }

            _logger.LogInformation(
                "RAG auto-index: indexing {Project}/{Repository} @ {Branch}",
                target.Project, target.Repository, target.Branch);

            try
            {
                // RefreshIndexAsync reuses the existing git clone on the file share if present
                // (git fetch + diff, re-embeds only changed files). Falls back to full clone
                // if the clone directory doesn't exist yet (first boot after wipe).
                var result = await _codebaseContextService.RefreshIndexAsync(
                    target.Project, target.Repository, target.Branch,
                    accessTokenOverride: _adoConfig.PersonalAccessToken);

                _logger.LogInformation(
                    "RAG auto-index: {Repository} ready — {Result}",
                    target.Repository,
                    result switch
                    {
                        0  => "already up-to-date",
                        -1 => "full re-index completed",
                        _  => $"{result} chunks refreshed"
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "RAG auto-index: failed to index {Project}/{Repository}",
                    target.Project, target.Repository);
            }
        }

        _logger.LogInformation("RAG auto-index: completed");
    }

    private static List<AutoIndexTarget> ParseTargets(string? raw)
    {
        var results = new List<AutoIndexTarget>();
        if (string.IsNullOrWhiteSpace(raw)) return results;

        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Format: Project/Repository:branch  (branch is optional, defaults to "main")
            var branchParts = entry.Split(':', 2);
            var repoParts   = branchParts[0].Split('/', 2);

            if (repoParts.Length != 2) continue;

            results.Add(new AutoIndexTarget
            {
                Project    = repoParts[0].Trim(),
                Repository = repoParts[1].Trim(),
                Branch     = branchParts.Length > 1 ? branchParts[1].Trim() : "main"
            });
        }

        return results;
    }

    /// <summary>
    /// Writes ~/.git-credentials so git can authenticate against ADO without an interactive
    /// TTY (headless containers have no terminal for git to prompt on).
    /// Also sets credential.helper=store globally.
    /// </summary>
    private void ConfigureGitCredentials(string pat)
    {
        if (string.IsNullOrWhiteSpace(pat))
        {
            _logger.LogWarning("RAG auto-index: PAT is empty — skipping git credential setup");
            return;
        }

        try
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/root";
            var credFile = Path.Combine(home, ".git-credentials");

            // Cover all three hostname variants ADO/VSTS uses
            var lines = new[]
            {
                $"https://git:{pat}@dev.azure.com",
                $"https://git:{pat}@skype.visualstudio.com",
                $"https://git:{pat}@spool.visualstudio.com",
            };
            File.WriteAllLines(credFile, lines);
            _logger.LogInformation("RAG auto-index: wrote git credentials to {CredFile}", credFile);

            // Tell git to use the store helper
            RunGit("config --global credential.helper store");
            _logger.LogInformation("RAG auto-index: git credential.helper set to 'store'");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RAG auto-index: failed to configure git credentials");
        }
    }

    private static void RunGit(string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git process");
        proc.WaitForExit(10_000);
    }

    private class AutoIndexTarget
    {
        public string Project    { get; set; } = string.Empty;
        public string Repository { get; set; } = string.Empty;
        public string Branch     { get; set; } = "main";
    }
}
