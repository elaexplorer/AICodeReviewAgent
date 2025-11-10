using System.Text.Json;
using CodeReviewAgent.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace CodeReviewAgent.Services;

public class AzureDevOpsMcpClient : IAsyncDisposable
{
    private readonly ILogger<AzureDevOpsMcpClient> _logger;
    private readonly string _organization;
    private readonly string _personalAccessToken;
    private readonly string _project;
    private McpClient? _mcpClient;
    private readonly AzureDevOpsRestClient _restClient;
    private readonly CodebaseCache _codebaseCache;

    public AzureDevOpsMcpClient(
        ILogger<AzureDevOpsMcpClient> logger,
        string organization,
        string personalAccessToken,
        string project,
        AzureDevOpsRestClient restClient,
        CodebaseCache codebaseCache)
    {
        _logger = logger;
        _organization = organization;
        _personalAccessToken = personalAccessToken;
        _project = project;
        _restClient = restClient;
        _codebaseCache = codebaseCache;
    }

    private async Task<McpClient> GetMcpClientAsync()
    {
        if (_mcpClient == null)
        {
            _logger.LogInformation("Initializing MCP client for Azure DevOps");

            var orgUrl = $"https://{_organization}.visualstudio.com";

            var transportOptions = new StdioClientTransportOptions
            {
                Command = "npx",
                Arguments = ["-y", "@azure-devops/mcp", _organization, "-a", "env"],
                Name = "AzureDevOps",
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["AZURE_DEVOPS_EXT_PAT"] = _personalAccessToken
                }
            };

            var clientTransport = new StdioClientTransport(transportOptions);
            _mcpClient = await McpClient.CreateAsync(clientTransport);

            _logger.LogInformation("MCP client initialized successfully");
        }
        return _mcpClient;
    }

    public async Task<PullRequest?> GetPullRequestAsync(string project, string repository, int pullRequestId)
    {
        // Try REST API first as it's more reliable
        try
        {
            _logger.LogInformation("Fetching PR {PullRequestId} from repository {Repository} (project {Project}) via REST API",
                pullRequestId, repository, project);

            var pullRequest = await _restClient.GetPullRequestAsync(project, repository, pullRequestId);
            if (pullRequest != null)
            {
                _logger.LogInformation("Found PR {PullRequestId}: {Title}", pullRequest.Id, pullRequest.Title);
                return pullRequest;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching pull request {PullRequestId} via REST API, trying MCP", pullRequestId);
        }

        // Fallback to MCP if REST API fails
        try
        {
            _logger.LogInformation("Fetching PR {PullRequestId} from repository {Repository} (project {Project}) via MCP",
                pullRequestId, repository, project);

            var client = await GetMcpClientAsync();

            // First, get the repository ID by name using repo_get_repo_by_name_or_id
            var repoResult = await client.CallToolAsync("repo_get_repo_by_name_or_id", new Dictionary<string, object?>
            {
                ["project"] = project,
                ["repositoryNameOrId"] = repository
            });

            string? repositoryId = null;

            if (repoResult.Content?.Count > 0)
            {
                var repoContent = JsonSerializer.Serialize(repoResult.Content[0]);
                var repoData = JsonSerializer.Deserialize<JsonElement>(repoContent);
                var textValue = repoData.TryGetProperty("text", out var textProp) ? textProp.GetString() : repoContent;
                var repo = JsonSerializer.Deserialize<JsonElement>(textValue!);

                repositoryId = repo.GetProperty("id").GetString();
                _logger.LogInformation("Found repository {Repository} with ID {RepositoryId}", repository, repositoryId);
            }

            if (string.IsNullOrEmpty(repositoryId))
            {
                _logger.LogError("Repository {Repository} not found in project {Project}", repository, project);
                return null;
            }

            // Use the repo_get_pull_request_by_id tool to directly fetch the PR by ID
            _logger.LogInformation("Fetching PR {PullRequestId} using repo_get_pull_request_by_id", pullRequestId);

            var result = await client.CallToolAsync("repo_get_pull_request_by_id", new Dictionary<string, object?>
            {
                ["repositoryId"] = repositoryId,
                ["pullRequestId"] = pullRequestId,
                ["includeWorkItemRefs"] = false
            });

            if (result.Content?.Count > 0)
            {
                var prContent = JsonSerializer.Serialize(result.Content[0]);
                var contentData = JsonSerializer.Deserialize<JsonElement>(prContent);
                var prTextValue = contentData.TryGetProperty("text", out var prTextProp) ? prTextProp.GetString() : prContent;

                var prData = JsonSerializer.Deserialize<JsonElement>(prTextValue!);

                var pullRequest = new PullRequest
                {
                    Id = prData.GetProperty("pullRequestId").GetInt32(),
                    Title = prData.GetProperty("title").GetString() ?? string.Empty,
                    Description = prData.TryGetProperty("description", out var desc) ? desc.GetString() ?? string.Empty : string.Empty,
                    SourceBranch = prData.GetProperty("sourceRefName").GetString() ?? string.Empty,
                    TargetBranch = prData.GetProperty("targetRefName").GetString() ?? string.Empty,
                    CreatedBy = prData.TryGetProperty("createdBy", out var creator)
                        ? new PullRequestUser
                        {
                            DisplayName = creator.TryGetProperty("displayName", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                            UniqueName = creator.TryGetProperty("uniqueName", out var unique) ? unique.GetString() ?? string.Empty : string.Empty
                        }
                        : new PullRequestUser(),
                    CreationDate = prData.TryGetProperty("creationDate", out var date)
                        ? date.GetDateTime()
                        : DateTime.Now,
                    Status = prData.TryGetProperty("status", out var statusProp) ? statusProp.GetInt32().ToString() : "unknown"
                };

                _logger.LogInformation("Found PR {PullRequestId}: {Title}", pullRequest.Id, pullRequest.Title);
                return pullRequest;
            }

            _logger.LogWarning("PR {PullRequestId} not found in repository {Repository}", pullRequestId, repository);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching pull request {PullRequestId} via MCP", pullRequestId);
            return null;
        }
    }

    public async Task<List<PullRequestFile>> GetPullRequestFilesAsync(string project, string repository, int pullRequestId)
    {
        try
        {
            _logger.LogInformation("Fetching files for PR {PullRequestId} from repository {Repository}",
                pullRequestId, repository);

            // Get repository ID first
            var client = await GetMcpClientAsync();
            var repoResult = await client.CallToolAsync("repo_get_repo_by_name_or_id", new Dictionary<string, object?>
            {
                ["project"] = project,
                ["repositoryNameOrId"] = repository
            });

            string? repositoryId = null;
            if (repoResult.Content?.Count > 0)
            {
                var repoContent = JsonSerializer.Serialize(repoResult.Content[0]);
                var repoData = JsonSerializer.Deserialize<JsonElement>(repoContent);
                var textValue = repoData.TryGetProperty("text", out var textProp) ? textProp.GetString() : repoContent;
                var repo = JsonSerializer.Deserialize<JsonElement>(textValue!);
                repositoryId = repo.GetProperty("id").GetString();
            }

            if (string.IsNullOrEmpty(repositoryId))
            {
                _logger.LogError("Repository {Repository} not found", repository);
                return new List<PullRequestFile>();
            }

            // Use REST API to get file changes (MCP doesn't support this yet)
            var files = await _restClient.GetPullRequestChangesAsync(project, repositoryId, pullRequestId);

            _logger.LogInformation("Found {FileCount} changed files in PR {PullRequestId}", files.Count, pullRequestId);
            return files;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching pull request files for PR {PullRequestId}", pullRequestId);
            return new List<PullRequestFile>();
        }
    }

    /// <summary>
    /// Get and cache repository structure for context understanding
    /// </summary>
    public async Task<List<string>> GetRepositoryStructureAsync(string project, string repository, string branch = "master")
    {
        try
        {
            // Get repository ID first
            var client = await GetMcpClientAsync();
            var repoResult = await client.CallToolAsync("repo_get_repo_by_name_or_id", new Dictionary<string, object?>
            {
                ["project"] = project,
                ["repositoryNameOrId"] = repository
            });

            string? repositoryId = null;
            if (repoResult.Content?.Count > 0)
            {
                var repoContent = JsonSerializer.Serialize(repoResult.Content[0]);
                var repoData = JsonSerializer.Deserialize<JsonElement>(repoContent);
                var textValue = repoData.TryGetProperty("text", out var textProp) ? textProp.GetString() : repoContent;
                var repo = JsonSerializer.Deserialize<JsonElement>(textValue!);
                repositoryId = repo.GetProperty("id").GetString();
            }

            if (string.IsNullOrEmpty(repositoryId))
            {
                _logger.LogError("Repository {Repository} not found", repository);
                return new List<string>();
            }

            // Check cache first
            var cached = _codebaseCache.GetCachedRepositoryStructure(repositoryId, branch);
            if (cached != null)
            {
                return cached;
            }

            // Fetch from REST API
            var files = await _restClient.GetRepositoryItemsAsync(project, repositoryId, branch);

            // Cache the structure
            _codebaseCache.CacheRepositoryStructure(repositoryId, branch, files);

            return files;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching repository structure");
            return new List<string>();
        }
    }

    public async Task<bool> PostCommentAsync(string project, string repository, int pullRequestId, CodeReviewComment comment)
    {
        // Try REST API first as MCP doesn't support posting comments
        try
        {
            _logger.LogInformation("Posting comment to PR {PullRequestId} in repository {Repository} via REST API",
                pullRequestId, repository);

            // Get repository ID first
            var repoResult = await GetMcpClientAsync();
            var repoResponse = await repoResult.CallToolAsync("repo_get_repo_by_name_or_id", new Dictionary<string, object?>
            {
                ["project"] = project,
                ["repositoryNameOrId"] = repository
            });

            string? repositoryId = null;
            if (repoResponse.Content?.Count > 0)
            {
                var repoContent = JsonSerializer.Serialize(repoResponse.Content[0]);
                var repoData = JsonSerializer.Deserialize<JsonElement>(repoContent);
                var textValue = repoData.TryGetProperty("text", out var textProp) ? textProp.GetString() : repoContent;
                var repo = JsonSerializer.Deserialize<JsonElement>(textValue!);
                repositoryId = repo.GetProperty("id").GetString();
            }

            if (string.IsNullOrEmpty(repositoryId))
            {
                _logger.LogError("Repository {Repository} not found", repository);
                return false;
            }

            // Use REST API to post comment
            var success = await _restClient.PostPullRequestCommentAsync(project, repositoryId, pullRequestId, comment);
            if (success)
            {
                _logger.LogInformation("Posted comment to PR {PullRequestId}: {Comment}", pullRequestId, comment.CommentText);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting comment to PR {PullRequestId}", pullRequestId);
            return false;
        }
    }

    public async Task<List<PullRequest>> GetActivePullRequestsAsync(string project, string repository)
    {
        try
        {
            _logger.LogInformation("Fetching active PRs for {Project}/{Repository}", project, repository);

            // Use REST API to get active pull requests
            var prs = await _restClient.GetActivePullRequestsAsync(project, repository);

            _logger.LogInformation("Found {Count} active PRs in {Repository}", prs.Count, repository);
            return prs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching active pull requests");
            return new List<PullRequest>();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient is not null)
        {
            // Dispose the MCP client
            _mcpClient = null;
        }

        GC.SuppressFinalize(this);
        await Task.CompletedTask;
    }
}
