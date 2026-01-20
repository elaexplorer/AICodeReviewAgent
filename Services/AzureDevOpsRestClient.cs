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
    private HttpClient _httpClient;
    private string _organization;
    private string _personalAccessToken;

    public AzureDevOpsRestClient(
        ILogger<AzureDevOpsRestClient> logger,
        string organization,
        string personalAccessToken)
    {
        _logger = logger;
        _organization = organization;
        _personalAccessToken = personalAccessToken;

        InitializeHttpClient(organization, personalAccessToken);
    }

    private void InitializeHttpClient(string organization, string personalAccessToken)
    {
        _httpClient?.Dispose();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"https://dev.azure.com/{organization}/")
        };

        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{personalAccessToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void UpdateConfiguration(string organization, string personalAccessToken)
    {
        _organization = organization;
        _personalAccessToken = personalAccessToken;
        InitializeHttpClient(organization, personalAccessToken);
        _logger.LogInformation("Updated REST client configuration for organization: {Organization}", organization);
    }

    /// <summary>
    /// Get pull request details
    /// </summary>
    public async Task<PullRequest?> GetPullRequestAsync(
        string project,
        string repository,
        int pullRequestId)
    {
        try
        {
            _logger.LogInformation("Fetching PR {PullRequestId} in repository {Repository} (project {Project})",
                pullRequestId, repository, project);

            // Get repository ID first
            var repoUrl = $"{project}/_apis/git/repositories/{repository}?api-version=7.1";
            var repoResponse = await _httpClient.GetAsync(repoUrl);
            repoResponse.EnsureSuccessStatusCode();

            var repoContent = await repoResponse.Content.ReadAsStringAsync();
            var repoData = JsonSerializer.Deserialize<JsonElement>(repoContent);
            var repositoryId = repoData.GetProperty("id").GetString();

            if (string.IsNullOrEmpty(repositoryId))
            {
                _logger.LogError("Could not find repository ID for {Repository}", repository);
                return null;
            }

            // Get the PR details
            var prUrl = $"{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}?api-version=7.1";
            var prResponse = await _httpClient.GetAsync(prUrl);
            prResponse.EnsureSuccessStatusCode();

            var prContent = await prResponse.Content.ReadAsStringAsync();
            var prData = JsonSerializer.Deserialize<JsonElement>(prContent);

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
                Status = prData.TryGetProperty("status", out var statusProp)
                    ? (statusProp.ValueKind == JsonValueKind.Number ? statusProp.GetInt32().ToString() : statusProp.GetString() ?? "unknown")
                    : "unknown"
            };

            _logger.LogInformation("Found PR {PullRequestId}: {Title}", pullRequest.Id, pullRequest.Title);
            return pullRequest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching pull request {PullRequestId}", pullRequestId);
            return null;
        }
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

            // Get the PR to find its iterations and merge base
            var prUrl = $"{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}?api-version=7.1";
            var prResponse = await _httpClient.GetAsync(prUrl);
            prResponse.EnsureSuccessStatusCode();

            var prContent = await prResponse.Content.ReadAsStringAsync();
            var prData = JsonSerializer.Deserialize<JsonElement>(prContent);

            // Get the merge base commits from the PR data
            string? sourceCommitId = prData.TryGetProperty("lastMergeSourceCommit", out var sourceProp)
                ? sourceProp.GetProperty("commitId").GetString()
                : null;
            string? targetCommitId = prData.TryGetProperty("lastMergeTargetCommit", out var targetProp)
                ? targetProp.GetProperty("commitId").GetString()
                : null;

            _logger.LogDebug("PR {PullRequestId} - source commit: {Source}, target commit: {Target}",
                pullRequestId, sourceCommitId, targetCommitId);

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

                    // Get previous content if it's an edit or delete
                    // Try to get from the target (base) commit using the file path
                    if (changeType != "add" && !string.IsNullOrEmpty(targetCommitId))
                    {
                        try
                        {
                            // Get the file content from the base commit
                            var baseFileUrl = $"{project}/_apis/git/repositories/{repositoryId}/items?path={Uri.EscapeDataString(path)}&versionDescriptor.versionType=commit&versionDescriptor.version={targetCommitId}&$format=text&api-version=7.1";
                            var baseFileResponse = await _httpClient.GetAsync(baseFileUrl);
                            if (baseFileResponse.IsSuccessStatusCode)
                            {
                                file.PreviousContent = await baseFileResponse.Content.ReadAsStringAsync();
                                _logger.LogDebug("Fetched {PrevLength} bytes of previous content for {Path} from commit {Commit}",
                                    file.PreviousContent?.Length ?? 0, path, targetCommitId);
                            }
                            else
                            {
                                _logger.LogDebug("Could not fetch previous content for {Path} from commit {Commit}: {StatusCode}",
                                    path, targetCommitId, baseFileResponse.StatusCode);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error fetching previous content for {Path}", path);
                        }
                    }

                    // Generate the unified diff from content we already have
                    file.UnifiedDiff = GenerateUnifiedDiff(file.PreviousContent, file.Content);
                    _logger.LogDebug("Generated unified diff for {Path} ({Length} bytes)",
                        path, file.UnifiedDiff?.Length ?? 0);

                    files.Add(file);
                    _logger.LogDebug("Added file {Path} with change type {ChangeType}, has content: {HasContent}, has previous: {HasPrevious}",
                        path, changeType, !string.IsNullOrEmpty(file.Content), !string.IsNullOrEmpty(file.PreviousContent));
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
    /// Generate unified diff from old and new content using Myers diff algorithm
    /// </summary>
    public string GenerateUnifiedDiff(string? previousContent, string? currentContent)
    {
        if (string.IsNullOrEmpty(previousContent) && string.IsNullOrEmpty(currentContent))
            return string.Empty;

        var oldLines = (previousContent ?? string.Empty).Split('\n');
        var newLines = (currentContent ?? string.Empty).Split('\n');

        var diffLines = new List<string>();
        var lcs = ComputeLCS(oldLines, newLines);

        int oldIndex = 0;
        int newIndex = 0;
        int lcsIndex = 0;

        while (oldIndex < oldLines.Length || newIndex < newLines.Length)
        {
            // Collect removals and additions for this hunk
            var hunkOldStart = oldIndex + 1;
            var hunkNewStart = newIndex + 1;
            var hunkLines = new List<string>();
            var oldCount = 0;
            var newCount = 0;

            // Add context before changes (up to 3 lines)
            var contextBefore = new List<string>();
            var tempLcsIndex = lcsIndex;
            for (int i = 0; i < 3 && tempLcsIndex < lcs.Count; i++, tempLcsIndex++)
            {
                if (lcs[tempLcsIndex].oldIndex == oldIndex + i && lcs[tempLcsIndex].newIndex == newIndex + i)
                {
                    contextBefore.Add($" {oldLines[oldIndex + i]}");
                }
            }

            // Process changes
            while (oldIndex < oldLines.Length || newIndex < newLines.Length)
            {
                if (lcsIndex < lcs.Count &&
                    oldIndex == lcs[lcsIndex].oldIndex &&
                    newIndex == lcs[lcsIndex].newIndex)
                {
                    // Common line - include as context and break if we have changes
                    if (hunkLines.Count > 0)
                    {
                        // Add up to 3 lines of context after
                        for (int i = 0; i < 3 && lcsIndex < lcs.Count; i++, lcsIndex++, oldIndex++, newIndex++)
                        {
                            hunkLines.Add($" {oldLines[oldIndex]}");
                            oldCount++;
                            newCount++;
                        }
                        break;
                    }
                    oldIndex++;
                    newIndex++;
                    lcsIndex++;
                }
                else if (oldIndex < oldLines.Length &&
                        (lcsIndex >= lcs.Count || oldIndex != lcs[lcsIndex].oldIndex))
                {
                    // Removed line
                    hunkLines.Add($"-{oldLines[oldIndex]}");
                    oldIndex++;
                    oldCount++;
                }
                else if (newIndex < newLines.Length &&
                        (lcsIndex >= lcs.Count || newIndex != lcs[lcsIndex].newIndex))
                {
                    // Added line
                    hunkLines.Add($"+{newLines[newIndex]}");
                    newIndex++;
                    newCount++;
                }
                else
                {
                    break;
                }
            }

            // Add hunk if we have changes
            if (hunkLines.Count > 0)
            {
                diffLines.Add($"@@ -{hunkOldStart},{oldCount} +{hunkNewStart},{newCount} @@");
                diffLines.AddRange(hunkLines);
            }
        }

        return string.Join("\n", diffLines);
    }

    private List<(int oldIndex, int newIndex, string line)> ComputeLCS(string[] oldLines, string[] newLines)
    {
        int m = oldLines.Length;
        int n = newLines.Length;
        int[,] dp = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                if (oldLines[i - 1] == newLines[j - 1])
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                else
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        var lcs = new List<(int, int, string)>();
        int oi = m, ni = n;
        while (oi > 0 && ni > 0)
        {
            if (oldLines[oi - 1] == newLines[ni - 1])
            {
                lcs.Insert(0, (oi - 1, ni - 1, oldLines[oi - 1]));
                oi--;
                ni--;
            }
            else if (dp[oi - 1, ni] > dp[oi, ni - 1])
                oi--;
            else
                ni--;
        }

        return lcs;
    }

    /// <summary>
    /// Get file content by object ID (blob ID)
    /// </summary>
    private async Task<string> GetFileContentByObjectIdAsync(string project, string repositoryId, string objectId)
    {
        try
        {
            // Add $format=text to get raw content instead of JSON metadata
            var url = $"{project}/_apis/git/repositories/{repositoryId}/blobs/{objectId}?$format=text&api-version=7.1";
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
    /// Get file content at a specific commit or branch
    /// </summary>
    public async Task<string> GetFileContentAsync(
        string project,
        string repositoryId,
        string path,
        string versionOrBranch = "main")
    {
        try
        {
            // Add $format=text to get raw content instead of JSON metadata
            var url = $"{project}/_apis/git/repositories/{repositoryId}/items?path={Uri.EscapeDataString(path)}&versionDescriptor.version={versionOrBranch}&$format=text&api-version=7.1";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error fetching file content for {Path} at {Version}", path, versionOrBranch);
            return string.Empty;
        }
    }

    /// <summary>
    /// Post a comment to a pull request
    /// </summary>
    public async Task<bool> PostPullRequestCommentAsync(
        string project,
        string repositoryId,
        int pullRequestId,
        CodeReviewComment comment)
    {
        try
        {
            _logger.LogInformation("Posting comment to PR {PullRequestId} via REST API", pullRequestId);

            // Create a thread with a comment
            var commentPayload = new
            {
                comments = new[]
                {
                    new
                    {
                        parentCommentId = 0,
                        content = $"**[{comment.Severity.ToUpper()}] {comment.CommentType}** (Line {comment.LineNumber})\n\n{comment.CommentText}",
                        commentType = 1 // Text comment
                    }
                },
                status = 1 // Active
            };

            var url = $"{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads?api-version=7.1";
            var json = System.Text.Json.JsonSerializer.Serialize(commentPayload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Successfully posted comment to PR {PullRequestId}", pullRequestId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting comment to PR {PullRequestId}", pullRequestId);
            return false;
        }
    }

    /// <summary>
    /// Get active pull requests for a repository
    /// </summary>
    public async Task<List<PullRequest>> GetActivePullRequestsAsync(string project, string repository)
    {
        try
        {
            _logger.LogInformation("Fetching active PRs for {Project}/{Repository}", project, repository);

            // Get repository ID first
            var repoUrl = $"{project}/_apis/git/repositories/{repository}?api-version=7.1";
            var repoResponse = await _httpClient.GetAsync(repoUrl);
            repoResponse.EnsureSuccessStatusCode();

            var repoContent = await repoResponse.Content.ReadAsStringAsync();
            var repoData = JsonSerializer.Deserialize<JsonElement>(repoContent);
            var repositoryId = repoData.GetProperty("id").GetString();

            if (string.IsNullOrEmpty(repositoryId))
            {
                _logger.LogError("Could not find repository ID for {Repository}", repository);
                return new List<PullRequest>();
            }

            // Get active pull requests
            var prUrl = $"{project}/_apis/git/repositories/{repositoryId}/pullRequests?searchCriteria.status=active&api-version=7.1";
            var prResponse = await _httpClient.GetAsync(prUrl);
            prResponse.EnsureSuccessStatusCode();

            var prContent = await prResponse.Content.ReadAsStringAsync();
            var prData = JsonSerializer.Deserialize<JsonElement>(prContent);

            var pullRequests = new List<PullRequest>();

            if (prData.TryGetProperty("value", out var prs))
            {
                foreach (var pr in prs.EnumerateArray())
                {
                    var pullRequest = new PullRequest
                    {
                        Id = pr.GetProperty("pullRequestId").GetInt32(),
                        Title = pr.GetProperty("title").GetString() ?? string.Empty,
                        Description = pr.TryGetProperty("description", out var desc) ? desc.GetString() ?? string.Empty : string.Empty,
                        SourceBranch = pr.GetProperty("sourceRefName").GetString() ?? string.Empty,
                        TargetBranch = pr.GetProperty("targetRefName").GetString() ?? string.Empty,
                        CreatedBy = pr.TryGetProperty("createdBy", out var creator)
                            ? new PullRequestUser
                            {
                                DisplayName = creator.TryGetProperty("displayName", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                                UniqueName = creator.TryGetProperty("uniqueName", out var unique) ? unique.GetString() ?? string.Empty : string.Empty
                            }
                            : new PullRequestUser(),
                        CreationDate = pr.TryGetProperty("creationDate", out var date)
                            ? date.GetDateTime()
                            : DateTime.Now,
                        Status = pr.TryGetProperty("status", out var statusProp)
                            ? (statusProp.ValueKind == JsonValueKind.Number ? statusProp.GetInt32().ToString() : statusProp.GetString() ?? "unknown")
                            : "unknown"
                    };

                    pullRequests.Add(pullRequest);
                }
            }

            _logger.LogInformation("Found {Count} active PRs in {Repository}", pullRequests.Count, repository);
            return pullRequests;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching active pull requests for {Project}/{Repository}", project, repository);
            return new List<PullRequest>();
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
            _logger.LogInformation("========================================");
            _logger.LogInformation("RAG INDEXING: Fetching repository structure");
            _logger.LogInformation("Repository ID: {RepositoryId}", repositoryId);
            _logger.LogInformation("Branch: {Branch}", branch);
            _logger.LogInformation("Scope Path: {ScopePath}", scopePath);

            var url = $"{project}/_apis/git/repositories/{repositoryId}/items?scopePath={Uri.EscapeDataString(scopePath)}&recursionLevel=Full&versionDescriptor.version={branch}&api-version=7.1";
            _logger.LogInformation("API URL: {Url}", url);

            _logger.LogInformation("Calling Azure DevOps API...");
            var response = await _httpClient.GetAsync(url);

            _logger.LogInformation("Response Status: {StatusCode}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("API Error Response: {Error}", errorContent);
                response.EnsureSuccessStatusCode();
            }

            var content = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Response Content Length: {Length} bytes", content.Length);

            var data = JsonSerializer.Deserialize<JsonElement>(content);

            var files = new List<string>();
            int totalItems = 0;
            int folders = 0;

            if (data.TryGetProperty("value", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    totalItems++;

                    // Check if it's a folder - items without isFolder property are treated as files
                    bool isFolderValue = false;
                    if (item.TryGetProperty("isFolder", out var isFolder))
                    {
                        isFolderValue = isFolder.GetBoolean();
                    }

                    if (isFolderValue)
                    {
                        folders++;
                        if (item.TryGetProperty("path", out var folderPath))
                        {
                            _logger.LogDebug("Found folder: {Path}", folderPath.GetString());
                        }
                    }
                    else
                    {
                        // It's a file - get its path
                        if (item.TryGetProperty("path", out var pathProp))
                        {
                            var path = pathProp.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(path))
                            {
                                files.Add(path);
                                _logger.LogDebug("Found file: {Path}", path);
                            }
                        }
                    }
                }
            }
            else
            {
                _logger.LogWarning("Response does not contain 'value' property");
                _logger.LogWarning("Response: {Response}", content.Length > 500 ? content.Substring(0, 500) + "..." : content);
            }

            _logger.LogInformation("RAG INDEXING: Summary");
            _logger.LogInformation("  Total items: {Total}", totalItems);
            _logger.LogInformation("  Folders: {Folders}", folders);
            _logger.LogInformation("  Files: {Files}", files.Count);
            _logger.LogInformation("========================================");

            return files;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RAG INDEXING ERROR: Failed to fetch repository items");
            _logger.LogError("  Repository: {RepositoryId}", repositoryId);
            _logger.LogError("  Branch: {Branch}", branch);
            _logger.LogError("  Error: {Message}", ex.Message);
            _logger.LogError("========================================");
            return new List<string>();
        }
    }
}
