using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeReviewAgent.Models;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// REST API client for Azure DevOps operations not available in MCP
/// </summary>
public class AzureDevOpsRestClient
{
    private readonly ILogger<AzureDevOpsRestClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _organization;
    private readonly string _personalAccessToken;

    public AzureDevOpsRestClient(
        ILogger<AzureDevOpsRestClient> logger,
        string organization,
        string personalAccessToken)
    {
        _logger = logger;
        _organization = organization;
        _personalAccessToken = personalAccessToken;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"https://{organization}.visualstudio.com/")
        };

        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{personalAccessToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Get file changes for a pull request
    /// </summary>
    public async Task<List<PullRequestFile>> GetPullRequestChangesAsync(
        string project,
        string repositoryId,
        int pullRequestId)
    {
        try
        {
            _logger.LogInformation("Fetching file changes for PR {PullRequestId} in repository {RepositoryId}",
                pullRequestId, repositoryId);

            // Get the PR to find its iterations
            var prUrl = $"{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}?api-version=7.1";
            var prResponse = await _httpClient.GetAsync(prUrl);
            prResponse.EnsureSuccessStatusCode();

            var prContent = await prResponse.Content.ReadAsStringAsync();
            var prData = JsonSerializer.Deserialize<JsonElement>(prContent);

            // Get the latest iteration (or we could get all iterations)
            var iterationsUrl = $"{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/iterations?api-version=7.1";
            var iterationsResponse = await _httpClient.GetAsync(iterationsUrl);
            iterationsResponse.EnsureSuccessStatusCode();

            var iterationsContent = await iterationsResponse.Content.ReadAsStringAsync();
            var iterationsData = JsonSerializer.Deserialize<JsonElement>(iterationsContent);

            var iterations = iterationsData.GetProperty("value").EnumerateArray().ToList();
            if (iterations.Count == 0)
            {
                _logger.LogWarning("No iterations found for PR {PullRequestId}", pullRequestId);
                return new List<PullRequestFile>();
            }

            // Get the latest iteration
            var latestIteration = iterations.Last();
            var iterationId = latestIteration.GetProperty("id").GetInt32();

            // Get changes for this iteration
            var changesUrl = $"{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/iterations/{iterationId}/changes?api-version=7.1";
            var changesResponse = await _httpClient.GetAsync(changesUrl);
            changesResponse.EnsureSuccessStatusCode();

            var changesContent = await changesResponse.Content.ReadAsStringAsync();
            var changesData = JsonSerializer.Deserialize<JsonElement>(changesContent);

            var files = new List<PullRequestFile>();

            if (changesData.TryGetProperty("changeEntries", out var changeEntries))
            {
                foreach (var change in changeEntries.EnumerateArray())
                {
                    if (!change.TryGetProperty("item", out var item))
                        continue;

                    var changeType = change.TryGetProperty("changeType", out var ct) ? ct.GetString() ?? "edit" : "edit";
                    var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;

                    if (string.IsNullOrEmpty(path))
                        continue;

                    // Skip folders
                    if (item.TryGetProperty("isFolder", out var isFolder) && isFolder.GetBoolean())
                        continue;

                    var file = new PullRequestFile
                    {
                        Path = path,
                        ChangeType = changeType
                    };

                    // Get file content from the target branch (modified version)
                    if (changeType != "delete" && item.TryGetProperty("objectId", out var objectId))
                    {
                        var objIdStr = objectId.GetString();
                        if (!string.IsNullOrEmpty(objIdStr))
                        {
                            var fileContent = await GetFileContentByObjectIdAsync(project, repositoryId, objIdStr);
                            file.Content = fileContent;
                        }
                    }

                    // Get previous content if it's an edit
                    if (changeType == "edit" && prData.TryGetProperty("lastMergeSourceCommit", out var sourceCommit))
                    {
                        if (sourceCommit.TryGetProperty("commitId", out var commitIdProp))
                        {
                            var sourceCommitId = commitIdProp.GetString();
                            if (!string.IsNullOrEmpty(sourceCommitId))
                            {
                                var previousContent = await GetFileContentAsync(project, repositoryId, path, sourceCommitId);
                                file.PreviousContent = previousContent;
                            }
                        }
                    }

                    files.Add(file);
                    _logger.LogDebug("Added file {Path} with change type {ChangeType}", path, changeType);
                }
            }

            _logger.LogInformation("Found {FileCount} changed files in PR {PullRequestId}", files.Count, pullRequestId);
            return files;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching pull request changes for PR {PullRequestId}", pullRequestId);
            return new List<PullRequestFile>();
        }
    }

    /// <summary>
    /// Get file content by object ID (blob ID)
    /// </summary>
    private async Task<string> GetFileContentByObjectIdAsync(string project, string repositoryId, string objectId)
    {
        try
        {
            var url = $"{project}/_apis/git/repositories/{repositoryId}/blobs/{objectId}?api-version=7.1";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching file content for object {ObjectId}", objectId);
            return string.Empty;
        }
    }

    /// <summary>
    /// Get file content at a specific commit
    /// </summary>
    private async Task<string> GetFileContentAsync(string project, string repositoryId, string path, string commitId)
    {
        try
        {
            var url = $"{project}/_apis/git/repositories/{repositoryId}/items?path={Uri.EscapeDataString(path)}&version={commitId}&api-version=7.1";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching file content for {Path} at commit {CommitId}", path, commitId);
            return string.Empty;
        }
    }

    /// <summary>
    /// Get repository items (file tree) for understanding codebase structure
    /// </summary>
    public async Task<List<string>> GetRepositoryItemsAsync(
        string project,
        string repositoryId,
        string branch = "master",
        string scopePath = "/")
    {
        try
        {
            _logger.LogInformation("Fetching repository structure for {RepositoryId} on branch {Branch}",
                repositoryId, branch);

            var url = $"{project}/_apis/git/repositories/{repositoryId}/items?scopePath={Uri.EscapeDataString(scopePath)}&recursionLevel=Full&versionDescriptor.version={branch}&api-version=7.1";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(content);

            var files = new List<string>();

            if (data.TryGetProperty("value", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("isFolder", out var isFolder) && !isFolder.GetBoolean())
                    {
                        if (item.TryGetProperty("path", out var pathProp))
                        {
                            var path = pathProp.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(path))
                            {
                                files.Add(path);
                            }
                        }
                    }
                }
            }

            _logger.LogInformation("Found {FileCount} files in repository {RepositoryId}", files.Count, repositoryId);
            return files;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching repository items for {RepositoryId}", repositoryId);
            return new List<string>();
        }
    }
}
