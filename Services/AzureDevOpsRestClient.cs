using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private string _agentPublicUrl;

    public string Organization => _organization;
    public string PersonalAccessToken => _personalAccessToken;

    public AzureDevOpsRestClient(
        ILogger<AzureDevOpsRestClient> logger,
        string organization,
        string personalAccessToken,
        string agentPublicUrl = "")
    {
        _logger = logger;
        _organization = organization;
        _personalAccessToken = personalAccessToken;
        _agentPublicUrl = agentPublicUrl.TrimEnd('/');

        InitializeHttpClient(organization, personalAccessToken);
    }

    private void InitializeHttpClient(string organization, string personalAccessToken)
    {
        _httpClient?.Dispose();
        
        // Use SocketsHttpHandler for better proxy compatibility
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials,
            PreAuthenticate = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2), // Refresh connections to avoid proxy issues
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
        };
        
        _httpClient = new HttpClient(handler);
        // Don't set BaseAddress - use full URLs instead to avoid proxy issues

        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{personalAccessToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "CodeReviewAgent/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public void UpdateConfiguration(string organization, string personalAccessToken)
    {
        _organization = organization;
        _personalAccessToken = personalAccessToken;
        InitializeHttpClient(organization, personalAccessToken);
        _logger.LogInformation("Updated REST client configuration for organization: {Organization}", organization);
    }

    /// <summary>
    /// Get repository information by name
    /// </summary>
    public async Task<RepositoryInfo?> GetRepositoryAsync(string project, string repositoryName)
    {
        try
        {
            var repoUrl = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryName}?api-version=7.1";
            var repoResponse = await _httpClient.GetAsync(repoUrl);
            repoResponse.EnsureSuccessStatusCode();

            var repoContent = await repoResponse.Content.ReadAsStringAsync();
            var repoData = JsonSerializer.Deserialize<JsonElement>(repoContent);

            return new RepositoryInfo
            {
                Id = repoData.GetProperty("id").GetString() ?? string.Empty,
                Name = repoData.GetProperty("name").GetString() ?? string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching repository {RepositoryName} in project {Project}", repositoryName, project);
            return null;
        }
    }

    /// <summary>
    /// Get pull request details
    /// </summary>
    public async Task<PullRequest?> GetPullRequestAsync(
        string project,
        string repository,
        int pullRequestId,
        string? accessTokenOverride = null)
    {
        try
        {
            _logger.LogInformation("Fetching PR {PullRequestId} in repository {Repository} (project {Project})",
                pullRequestId, repository, project);

            // Get repository ID first
            var repoUrl = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repository}?api-version=7.1";
            var repoResponse = await SendGetWithAuthFallbackAsync(repoUrl, accessTokenOverride, "repository lookup");
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
            var prUrl = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}?api-version=7.1";
            var prResponse = await SendGetWithAuthFallbackAsync(prUrl, accessTokenOverride, "pull request lookup");
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
            var prUrl = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}?api-version=7.1";
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
            var iterationsUrl = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/iterations?api-version=7.1";
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
            var changesUrl = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/iterations/{iterationId}/changes?api-version=7.1";
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
                            var baseFileUrl = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/items?path={Uri.EscapeDataString(path)}&versionDescriptor.versionType=commit&versionDescriptor.version={targetCommitId}&$format=text&api-version=7.1";
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
                    // Added line — annotate with absolute new-file line number so the AI
                    // can return an exact line number without parsing the @@ hunk header.
                    var rightFileLine = newIndex + 1;
                    hunkLines.Add($"+[L{rightFileLine}]{newLines[newIndex]}");
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
            var url = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/blobs/{objectId}?$format=text&api-version=7.1";
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
        string versionOrBranch = "master")
    {
        try
        {
            // Add $format=text to get raw content instead of JSON metadata
            var url = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/items?path={Uri.EscapeDataString(path)}&versionDescriptor.version={versionOrBranch}&$format=text&api-version=7.1";
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
        CodeReviewComment comment,
        string? accessTokenOverride = null)
    {
        var result = await PostPullRequestCommentDetailedAsync(project, repositoryId, pullRequestId, comment, accessTokenOverride);
        return result.Success;
    }

    public async Task<PullRequestCommentPostResult> PostPullRequestCommentDetailedAsync(
        string project,
        string repositoryId,
        int pullRequestId,
        CodeReviewComment comment,
        string? accessTokenOverride = null)
    {
        try
        {
            _logger.LogInformation("Posting comment to PR {PullRequestId} via REST API", pullRequestId);

            var url = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads?api-version=7.1";
            var normalizedFilePath = NormalizeThreadFilePath(comment.FilePath);
            var startLine = comment.StartLine > 0 ? comment.StartLine : 1;
            var endLine = comment.EndLine >= startLine ? comment.EndLine : startLine;
            var anchorBody = BuildCommentBody(comment) + BuildStampFooter(comment, _agentPublicUrl);

            // Attempt inline thread first so comments appear on the right file/line in the PR diff.
            if (!string.IsNullOrWhiteSpace(normalizedFilePath))
            {
                var inlinePayload = new
                {
                    comments = new[]
                    {
                        new
                        {
                            parentCommentId = 0,
                            content = anchorBody,
                            commentType = 1
                        }
                    },
                    status = 1,
                    threadContext = new
                    {
                        filePath = normalizedFilePath,
                        rightFileStart = new { line = startLine, offset = 1 },
                        rightFileEnd = new { line = endLine, offset = 1 }
                    }
                };

                var inlineJson = JsonSerializer.Serialize(inlinePayload);
                var inlineResponse = await SendPostWithAuthFallbackAsync(
                    url,
                    inlineJson,
                    accessTokenOverride,
                    "inline-thread-post");

                if (inlineResponse.IsSuccessStatusCode)
                {
                    var threadId = await ParseThreadIdFromResponseAsync(inlineResponse);
                    _logger.LogInformation(
                        "Successfully posted inline comment to PR {PullRequestId} at {FilePath}:{StartLine}-{EndLine} (threadId={ThreadId})",
                        pullRequestId,
                        normalizedFilePath,
                        startLine,
                        endLine,
                        threadId);
                    return PullRequestCommentPostResult.SuccessResult(threadId);
                }

                var inlineResponseText = await inlineResponse.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "Inline comment post failed for PR {PullRequestId} at {FilePath}:{StartLine}-{EndLine}. Status: {StatusCode}. Falling back to general thread. Body: {Body}",
                    pullRequestId,
                    normalizedFilePath,
                    startLine,
                    endLine,
                    inlineResponse.StatusCode,
                    inlineResponseText);
            }

            // Fallback to a general PR thread if inline anchoring cannot be applied.
            var generalPayload = new
            {
                comments = new[]
                {
                    new
                    {
                        parentCommentId = 0,
                        content = anchorBody,
                        commentType = 1
                    }
                },
                status = 1
            };

            var json = JsonSerializer.Serialize(generalPayload);
            var response = await SendPostWithAuthFallbackAsync(
                url,
                json,
                accessTokenOverride,
                "general-thread-post");

            if (!response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "General thread comment post failed for PR {PullRequestId}. Status: {StatusCode}. Body: {Body}",
                    pullRequestId,
                    response.StatusCode,
                    responseText);

                return PullRequestCommentPostResult.FailureResult(
                    stage: "general-thread",
                    statusCode: (int)response.StatusCode,
                    errorMessage: $"General thread post failed with HTTP {(int)response.StatusCode}.",
                    responseBody: responseText);
            }

            var generalThreadId = await ParseThreadIdFromResponseAsync(response);
            _logger.LogInformation("Successfully posted comment to PR {PullRequestId} (threadId={ThreadId})", pullRequestId, generalThreadId);
            return PullRequestCommentPostResult.SuccessResult(generalThreadId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting comment to PR {PullRequestId}", pullRequestId);
            return PullRequestCommentPostResult.FailureResult(
                stage: "exception",
                statusCode: null,
                errorMessage: ex.Message,
                responseBody: null);
        }
    }

    private static async Task<int?> ParseThreadIdFromResponseAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            var root = JsonSerializer.Deserialize<JsonElement>(body);
            if (root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
                return id;
        }
        catch { /* non-critical */ }
        return null;
    }

    /// <summary>
    /// Fetches all threads on a PR and returns those posted by CodeReviewAIAgent,
    /// with the comment ID (extracted from the feedback URL in the stamp) and the ADO thread status.
    /// </summary>
    public async Task<List<AgentThreadStatus>> GetAgentThreadStatusesAsync(
        string project, string repositoryId, int pullRequestId)
    {
        var results = new List<AgentThreadStatus>();
        try
        {
            var url = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads?api-version=7.1";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var root = JsonSerializer.Deserialize<JsonElement>(content);
            if (!root.TryGetProperty("value", out var threads) || threads.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var thread in threads.EnumerateArray())
            {
                if (!thread.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array)
                    continue;

                // Only look at the first comment (our agent's text)
                var firstComment = comments.EnumerateArray().FirstOrDefault();
                if (firstComment.ValueKind != JsonValueKind.Object)
                    continue;

                if (!firstComment.TryGetProperty("content", out var contentProp))
                    continue;

                var text = contentProp.GetString() ?? string.Empty;
                if (!text.Contains("Generated by CodeReviewAIAgent", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Extract commentId from the embedded feedback URL: ?id={commentId}&
                var commentId = ExtractCommentIdFromStamp(text);
                if (string.IsNullOrWhiteSpace(commentId))
                    continue;

                var threadId = thread.TryGetProperty("id", out var tid) && tid.TryGetInt32(out var tidVal) ? tidVal : 0;
                var status   = thread.TryGetProperty("status", out var st) ? st.GetString() ?? "active" : "active";

                results.Add(new AgentThreadStatus
                {
                    CommentId = commentId,
                    ThreadId  = threadId,
                    Status    = status
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch agent thread statuses for PR {PullRequestId}", pullRequestId);
        }
        return results;
    }

    private static string? ExtractCommentIdFromStamp(string commentBody)
    {
        // Feedback URL format: /api/codereview/feedback?id={commentId}&rating=...
        var match = Regex.Match(commentBody, @"[?&]id=([^&\s\)]+)");
        if (!match.Success) return null;
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    private static HttpRequestMessage BuildThreadPostRequest(string url, string payloadJson, string? accessTokenOverride)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };

        var authHeader = BuildTokenOverrideAuthHeader(accessTokenOverride);
        if (authHeader != null)
        {
            request.Headers.Authorization = authHeader;
        }

        return request;
    }

    private static AuthenticationHeaderValue? BuildTokenOverrideAuthHeader(string? accessTokenOverride)
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

        return string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    private static bool IsAuthFailureStatusCode(System.Net.HttpStatusCode statusCode)
    {
        return statusCode == System.Net.HttpStatusCode.Unauthorized
            || statusCode == System.Net.HttpStatusCode.Forbidden;
    }

    private async Task<HttpResponseMessage> SendGetWithAuthFallbackAsync(
        string url,
        string? accessTokenOverride,
        string operationName)
    {
        var overrideHeader = BuildTokenOverrideAuthHeader(accessTokenOverride);
        if (overrideHeader == null)
        {
            return await _httpClient.GetAsync(url);
        }

        using var preferredRequest = new HttpRequestMessage(HttpMethod.Get, url);
        preferredRequest.Headers.Authorization = overrideHeader;

        var preferredResponse = await _httpClient.SendAsync(preferredRequest);
        if (!IsAuthFailureStatusCode(preferredResponse.StatusCode))
        {
            return preferredResponse;
        }

        var responsePreview = await preferredResponse.Content.ReadAsStringAsync();
        _logger.LogWarning(
            "Bearer token authorization failed during {Operation}. Falling back to configured PAT. Status={StatusCode}, Body={Body}",
            operationName,
            preferredResponse.StatusCode,
            responsePreview.Length > 400 ? responsePreview.Substring(0, 400) : responsePreview);

        preferredResponse.Dispose();
        return await _httpClient.GetAsync(url);
    }

    private async Task<HttpResponseMessage> SendPostWithAuthFallbackAsync(
        string url,
        string payloadJson,
        string? accessTokenOverride,
        string operationName)
    {
        var overrideHeader = BuildTokenOverrideAuthHeader(accessTokenOverride);
        if (overrideHeader == null)
        {
            return await _httpClient.PostAsync(url, new StringContent(payloadJson, Encoding.UTF8, "application/json"));
        }

        using var preferredRequest = BuildThreadPostRequest(url, payloadJson, accessTokenOverride);
        var preferredResponse = await _httpClient.SendAsync(preferredRequest);
        if (!IsAuthFailureStatusCode(preferredResponse.StatusCode))
        {
            return preferredResponse;
        }

        var responsePreview = await preferredResponse.Content.ReadAsStringAsync();
        _logger.LogWarning(
            "Bearer token authorization failed during {Operation}. Falling back to configured PAT. Status={StatusCode}, Body={Body}",
            operationName,
            preferredResponse.StatusCode,
            responsePreview.Length > 400 ? responsePreview.Substring(0, 400) : responsePreview);

        preferredResponse.Dispose();
        return await _httpClient.PostAsync(url, new StringContent(payloadJson, Encoding.UTF8, "application/json"));
    }

    private static string NormalizeThreadFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        return filePath.StartsWith("/", StringComparison.Ordinal)
            ? filePath
            : "/" + filePath;
    }

    public static string BuildCommentBody(CodeReviewComment comment)
    {
        var startLine = comment.StartLine > 0 ? comment.StartLine : 1;
        var endLine = comment.EndLine >= startLine ? comment.EndLine : startLine;
        var lineRef = startLine == endLine ? $"Line {startLine}" : $"Lines {startLine}–{endLine}";
        var confidenceStr = comment.Confidence > 0.0 ? $" · confidence: {comment.Confidence:F2}" : "";
        var severityLabel = (comment.Severity?.ToLower() is "high" or "critical") ? "CRITICAL" : (comment.Severity?.ToUpper() ?? "MEDIUM");
        var anchorTitle = $"**[{severityLabel}] {comment.CommentType}**";
        var body = $"{anchorTitle} ({lineRef}{confidenceStr})\n\n{comment.CommentText}";
        if (!string.IsNullOrWhiteSpace(comment.SuggestedFix))
            body += $"\n\n**Suggested fix:**\n{comment.SuggestedFix}";
        return body;
    }

    public static string BuildStampFooter(CodeReviewComment comment, string agentPublicUrl)
    {
        var footer = new System.Text.StringBuilder();
        footer.Append("\n\n---\n🤖 *Generated by [CodeReviewAIAgent](https://github.com/elaexplorer/AICodeReviewAgent)*");
        if (!string.IsNullOrWhiteSpace(agentPublicUrl))
        {
            var baseUrl = $"{agentPublicUrl}/api/codereview/feedback";
            var helpful    = Uri.EscapeDataString("1");
            var notHelpful = Uri.EscapeDataString("0");
            footer.Append(
                $"&nbsp;&nbsp;·&nbsp;&nbsp;Was this helpful?&nbsp;" +
                $"[👍 Yes]({baseUrl}?id={Uri.EscapeDataString(comment.Id)}&rating={helpful})&nbsp;" +
                $"[👎 No]({baseUrl}?id={Uri.EscapeDataString(comment.Id)}&rating={notHelpful})");
        }
        return footer.ToString();
    }

    public static string BuildCommentFingerprint(CodeReviewComment comment)
    {
        return NormalizeCommentBody(BuildCommentBody(comment));
    }

    public static string NormalizeCommentBody(string? commentBody)
    {
        if (string.IsNullOrWhiteSpace(commentBody))
        {
            return string.Empty;
        }

        // Collapse whitespace so equivalent content with formatting differences dedupes.
        return Regex.Replace(commentBody.Trim(), "\\s+", " ");
    }

    public async Task<HashSet<string>> GetExistingCommentFingerprintsAsync(string project, string repositoryId, int pullRequestId)
    {
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var url = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads?api-version=7.1";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var root = JsonSerializer.Deserialize<JsonElement>(content);
            if (!root.TryGetProperty("value", out var threads) || threads.ValueKind != JsonValueKind.Array)
            {
                return fingerprints;
            }

            foreach (var thread in threads.EnumerateArray())
            {
                if (!thread.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var comment in comments.EnumerateArray())
                {
                    if (!comment.TryGetProperty("content", out var contentProp))
                    {
                        continue;
                    }

                    var text = contentProp.GetString();
                    var normalized = NormalizeCommentBody(text);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        fingerprints.Add(normalized);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch existing PR thread comments for dedupe on PR {PullRequestId}", pullRequestId);
        }

        return fingerprints;
    }

    public static string BuildCommentPositionKey(CodeReviewComment comment)
    {
        var path = NormalizeThreadFilePath(comment.FilePath).ToLowerInvariant();
        var line = comment.StartLine > 0 ? comment.StartLine : 1;
        var severity = (comment.Severity ?? string.Empty).Trim().ToLowerInvariant();
        var type = (comment.CommentType ?? string.Empty).Trim().ToLowerInvariant();
        return $"{path}|{line}|{severity}|{type}";
    }

    public async Task<HashSet<string>> GetExistingCommentPositionKeysAsync(string project, string repositoryId, int pullRequestId)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var url = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads?api-version=7.1";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var root = JsonSerializer.Deserialize<JsonElement>(content);
            if (!root.TryGetProperty("value", out var threads) || threads.ValueKind != JsonValueKind.Array)
            {
                return keys;
            }

            foreach (var thread in threads.EnumerateArray())
            {
                string? filePath = null;
                int lineNumber = 1;

                if (thread.TryGetProperty("threadContext", out var threadContext) && threadContext.ValueKind == JsonValueKind.Object)
                {
                    if (threadContext.TryGetProperty("filePath", out var fp))
                    {
                        filePath = fp.GetString();
                    }

                    if (threadContext.TryGetProperty("rightFileStart", out var start) &&
                        start.ValueKind == JsonValueKind.Object &&
                        start.TryGetProperty("line", out var lineProp) &&
                        lineProp.ValueKind == JsonValueKind.Number)
                    {
                        lineNumber = lineProp.GetInt32();
                    }
                }

                if (!thread.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var comment in comments.EnumerateArray())
                {
                    if (!comment.TryGetProperty("content", out var contentProp))
                    {
                        continue;
                    }

                    var text = contentProp.GetString() ?? string.Empty;
                    var match = Regex.Match(text, @"\*\*\[(?<sev>[^\]]+)\]\s+(?<type>[^*]+)\*\*", RegexOptions.IgnoreCase);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var severity = match.Groups["sev"].Value.Trim().ToLowerInvariant();
                    var type = match.Groups["type"].Value.Trim().ToLowerInvariant();
                    var normalizedPath = NormalizeThreadFilePath(filePath).ToLowerInvariant();

                    keys.Add($"{normalizedPath}|{lineNumber}|{severity}|{type}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch existing PR thread position keys for dedupe on PR {PullRequestId}", pullRequestId);
        }

        return keys;
    }

    /// <summary>
    /// Returns the last N commit messages that touched a specific file, formatted for
    /// LLM context injection. Helps the reviewer detect regressions — e.g. if a line
    /// was intentionally fixed in a previous commit and this PR reverts it.
    /// </summary>
    public async Task<string> GetFileCommitHistoryAsync(
        string project,
        string repositoryId,
        string filePath,
        int maxCommits = 5)
    {
        try
        {
            // Normalize path — ADO expects leading slash
            var normalizedPath = filePath.StartsWith('/') ? filePath : "/" + filePath;
            var url = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/commits" +
                      $"?searchCriteria.itemPath={Uri.EscapeDataString(normalizedPath)}" +
                      $"&$top={maxCommits}&api-version=7.1";

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var root = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(content);

            if (!root.TryGetProperty("value", out var commits) || commits.ValueKind != System.Text.Json.JsonValueKind.Array)
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            foreach (var commit in commits.EnumerateArray())
            {
                var sha = commit.TryGetProperty("commitId", out var id) ? id.GetString()?[..8] ?? "?" : "?";
                var msg = commit.TryGetProperty("comment", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                // First line of commit message only
                msg = msg.Split('\n')[0].Trim();
                if (msg.Length > 120) msg = msg[..120] + "…";

                var date = string.Empty;
                if (commit.TryGetProperty("author", out var author) &&
                    author.TryGetProperty("date", out var dateProp))
                    date = dateProp.GetString()?[..10] ?? string.Empty;

                var authorName = string.Empty;
                if (commit.TryGetProperty("author", out var auth2) &&
                    auth2.TryGetProperty("name", out var nameProp))
                    authorName = nameProp.GetString() ?? string.Empty;

                if (!string.IsNullOrEmpty(msg))
                    sb.AppendLine($"  - `{sha}` ({date}, {authorName}): {msg}");
            }

            return sb.Length > 0 ? sb.ToString() : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch commit history for {FilePath}", filePath);
            return string.Empty;
        }
    }

    /// <summary>
    /// Returns a markdown summary of existing human reviewer threads on a PR,
    /// formatted for injection into the LLM review prompt so the AI avoids duplicating feedback.
    /// Only includes active, non-system threads that have inline file context.
    /// </summary>
    public async Task<string> GetExistingThreadSummaryAsync(string project, string repositoryId, int pullRequestId)
    {
        const int MaxCommentsToInclude = 40;
        const int MaxCommentChars = 300;

        try
        {
            var url = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads?api-version=7.1";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var root = JsonSerializer.Deserialize<JsonElement>(content);
            if (!root.TryGetProperty("value", out var threads) || threads.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            var count = 0;

            foreach (var thread in threads.EnumerateArray())
            {
                if (count >= MaxCommentsToInclude) break;

                // Skip system-generated threads (PR status, vote, merge updates)
                if (thread.TryGetProperty("isDeleted", out var deleted) && deleted.GetBoolean()) continue;
                if (!thread.TryGetProperty("threadContext", out var threadContext) ||
                    threadContext.ValueKind != JsonValueKind.Object) continue;

                // Skip threads without file context (PR-level comments)
                if (!threadContext.TryGetProperty("filePath", out var fpProp)) continue;
                var filePath = fpProp.GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(filePath)) continue;

                var line = 0;
                if (threadContext.TryGetProperty("rightFileStart", out var start) &&
                    start.ValueKind == JsonValueKind.Object &&
                    start.TryGetProperty("line", out var lineProp))
                    line = lineProp.GetInt32();

                var status = thread.TryGetProperty("status", out var statusProp)
                    ? statusProp.GetString() ?? "active"
                    : "active";

                if (!thread.TryGetProperty("comments", out var comments) ||
                    comments.ValueKind != JsonValueKind.Array) continue;

                // First non-system comment is the reviewer's actual text
                foreach (var comment in comments.EnumerateArray())
                {
                    if (comment.TryGetProperty("commentType", out var ct) &&
                        ct.GetString() == "system") continue;

                    if (!comment.TryGetProperty("content", out var contentProp)) continue;
                    var text = (contentProp.GetString() ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    // Truncate very long comments
                    var truncated = text.Length > MaxCommentChars
                        ? text[..MaxCommentChars] + "…"
                        : text;

                    var lineRef = line > 0 ? $":{line}" : string.Empty;
                    sb.AppendLine($"- `{filePath}{lineRef}` [{status}]: {truncated}");
                    count++;
                    break; // one entry per thread
                }
            }

            if (sb.Length == 0) return string.Empty;

            var header = new System.Text.StringBuilder();
            header.AppendLine("## Existing Review Comments (already raised — do NOT duplicate these)");
            header.AppendLine(sb.ToString());
            return header.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch existing PR threads for context on PR {PullRequestId}", pullRequestId);
            return string.Empty;
        }
    }

    /// <summary>
    /// Get active pull requests for a repository
    /// </summary>
    public async Task<List<PullRequest>> GetActivePullRequestsAsync(string project, string repository)
    {
        try
        {
            _logger.LogInformation("========================================");
            _logger.LogInformation("FETCHING ACTIVE PRS");
            _logger.LogInformation("Organization: {Organization}", _organization);
            _logger.LogInformation("Project: {Project}", project);
            _logger.LogInformation("Repository: {Repository}", repository);

            // Get repository ID first
            var repoUrl = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repository}?api-version=7.1";
            _logger.LogInformation("Repository URL: {RepoUrl}", repoUrl);
            
            var repoResponse = await _httpClient.GetAsync(repoUrl);
            _logger.LogInformation("Repository response status: {Status}", repoResponse.StatusCode);
            
            var repoContent = await repoResponse.Content.ReadAsStringAsync();
            _logger.LogInformation("Repository response length: {Length} bytes", repoContent.Length);
            
            // Check if response is HTML (proxy intercepted)
            if (repoContent.TrimStart().StartsWith("<"))
            {
                _logger.LogError("Received HTML instead of JSON - proxy or authentication issue");
                _logger.LogError("Response status: {Status}", repoResponse.StatusCode);
                _logger.LogError("Response preview: {Preview}", repoContent.Length > 300 ? repoContent.Substring(0, 300) : repoContent);
                throw new Exception($"Authentication failed: Received HTML login page instead of JSON. Status: {repoResponse.StatusCode}");
            }
            
            if (!repoResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Repository API failed. Response preview: {Preview}", 
                    repoContent.Length > 500 ? repoContent.Substring(0, 500) : repoContent);
                
                // Log the full HTML to see what the proxy is returning
                _logger.LogError("Full HTML response: {Html}", repoContent);
                
                repoResponse.EnsureSuccessStatusCode();
            }

            // Log the first 200 chars to see if it's JSON or HTML
            _logger.LogInformation("Repository response preview: {Preview}", 
                repoContent.Length > 200 ? repoContent.Substring(0, 200) : repoContent);

            var repoData = JsonSerializer.Deserialize<JsonElement>(repoContent);
            var repositoryId = repoData.GetProperty("id").GetString();

            if (string.IsNullOrEmpty(repositoryId))
            {
                _logger.LogError("Could not find repository ID for {Repository}", repository);
                return new List<PullRequest>();
            }

            _logger.LogInformation("Repository ID: {RepositoryId}", repositoryId);

            // Get active pull requests
            var prUrl = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests?searchCriteria.status=active&api-version=7.1";
            _logger.LogInformation("PR URL: {PrUrl}", prUrl);
            
            var prResponse = await _httpClient.GetAsync(prUrl);
            _logger.LogInformation("PR response status: {Status}", prResponse.StatusCode);
            
            var prContent = await prResponse.Content.ReadAsStringAsync();
            _logger.LogInformation("PR response length: {Length} bytes", prContent.Length);
            
            if (!prResponse.IsSuccessStatusCode)
            {
                _logger.LogError("PR API failed. Response preview: {Preview}", 
                    prContent.Length > 500 ? prContent.Substring(0, 500) : prContent);
                prResponse.EnsureSuccessStatusCode();
            }

            // Log the first 200 chars to see if it's JSON or HTML
            _logger.LogInformation("PR response preview: {Preview}", 
                prContent.Length > 200 ? prContent.Substring(0, 200) : prContent);

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
    /// Get all branches in the repository
    /// </summary>
    public async Task<List<string>> GetRepositoryBranchesAsync(string project, string repositoryId)
    {
        try
        {
            _logger.LogInformation("🌿 FETCHING REPOSITORY BRANCHES:");
            _logger.LogInformation("   Repository ID: {RepositoryId}", repositoryId);
            
            var url = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/refs/heads?api-version=7.1";
            _logger.LogInformation("   API URL: {Url}", url);
            
            var response = await _httpClient.GetAsync(url);
            _logger.LogInformation("   Response Status: {StatusCode}", response.StatusCode);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("❌ Error fetching branches: {Error}", errorContent);
                return new List<string>();
            }
            
            var content = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(content);
            
            var branches = new List<string>();
            if (data.TryGetProperty("value", out var refs))
            {
                foreach (var refItem in refs.EnumerateArray())
                {
                    if (refItem.TryGetProperty("name", out var name))
                    {
                        var fullName = name.GetString();
                        if (fullName?.StartsWith("refs/heads/") == true)
                        {
                            var branchName = fullName.Substring("refs/heads/".Length);
                            branches.Add(branchName);
                            _logger.LogInformation("   🌿 Found branch: {Branch}", branchName);
                        }
                    }
                }
            }
            
            _logger.LogInformation("✅ Found {Count} branches total", branches.Count);
            return branches;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error fetching repository branches");
            return new List<string>();
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
            _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║ RAG INDEXING: Fetching repository structure (with pagination) ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("Repository ID: {RepositoryId}", repositoryId);
            _logger.LogInformation("Branch: {Branch}", branch);
            _logger.LogInformation("Scope Path: {ScopePath}", scopePath);

            var allFiles = new List<string>();
            int totalItems = 0;
            int folders = 0;
            int pageCount = 0;
            string? continuationToken = null;

            do
            {
                pageCount++;
                var baseUrl = $"https://dev.azure.com/{_organization}/{project}/_apis/git/repositories/{repositoryId}/items?scopePath={Uri.EscapeDataString(scopePath)}&recursionLevel=Full&versionDescriptor.version={branch}&api-version=7.1";
                
                var url = !string.IsNullOrEmpty(continuationToken) 
                    ? $"{baseUrl}&continuationToken={Uri.EscapeDataString(continuationToken)}"
                    : baseUrl;

                _logger.LogInformation("📄 PAGE {PageCount}: Fetching repository items...", pageCount);
                _logger.LogInformation("   API URL: {Url}", url);

                var response = await _httpClient.GetAsync(url);
                _logger.LogInformation("   Response Status: {StatusCode}", response.StatusCode);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("❌ API Error Response: {Error}", errorContent);
                    response.EnsureSuccessStatusCode();
                }

                var content = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("   Response Content Length: {Length} bytes", content.Length);
                
                // Log the full API response to see what we're actually getting
                _logger.LogInformation("   🔍 FULL API RESPONSE DEBUG:");
                _logger.LogInformation("   Response: {Content}", content.Length > 5000 ? content.Substring(0, 5000) + "... [TRUNCATED]" : content);

                // Check for continuation token in response headers
                var responseHeaders = response.Headers;
                continuationToken = null;
                
                if (responseHeaders.TryGetValues("x-ms-continuationtoken", out var tokenValues))
                {
                    continuationToken = tokenValues.FirstOrDefault();
                    _logger.LogInformation("   📄 Continuation token found: {Token}", continuationToken?.Substring(0, Math.Min(50, continuationToken?.Length ?? 0)) + "...");
                }

                var data = JsonSerializer.Deserialize<JsonElement>(content);
                int pageItems = 0;
                int pageFiles = 0;
                int pageFolders = 0;

                if (data.TryGetProperty("value", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        pageItems++;
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
                            pageFolders++;
                            if (item.TryGetProperty("path", out var folderPath))
                            {
                                _logger.LogDebug("   📁 Found folder: {Path}", folderPath.GetString());
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
                                    allFiles.Add(path);
                                    pageFiles++;
                                    _logger.LogDebug("   📄 Found file: {Path}", path);
                                }
                            }
                        }
                    }

                    _logger.LogInformation("   ✅ Page {PageCount} Summary:", pageCount);
                    _logger.LogInformation("      Items on page: {PageItems}", pageItems);
                    _logger.LogInformation("      Files on page: {PageFiles}", pageFiles);
                    _logger.LogInformation("      Folders on page: {PageFolders}", pageFolders);
                    _logger.LogInformation("      Total files so far: {TotalFiles}", allFiles.Count);
                }
                else
                {
                    _logger.LogWarning("   ⚠️  Response does not contain 'value' property");
                    _logger.LogWarning("   Response preview: {Response}", content.Length > 500 ? content.Substring(0, 500) + "..." : content);
                }

            } while (!string.IsNullOrEmpty(continuationToken) && pageCount < 100); // Safety limit

            _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║ RAG INDEXING: Repository Structure Complete                ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("📊 FINAL SUMMARY:");
            _logger.LogInformation("   Total pages fetched: {PageCount}", pageCount);
            _logger.LogInformation("   Total items: {Total}", totalItems);
            _logger.LogInformation("   Total folders: {Folders}", folders);
            _logger.LogInformation("   Total files: {Files}", allFiles.Count);
            
            if (pageCount >= 100)
            {
                _logger.LogWarning("⚠️  Hit pagination safety limit (100 pages) - repository may have more files");
            }
            
            _logger.LogInformation("════════════════════════════════════════════════════════════");

            return allFiles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ RAG INDEXING ERROR: Failed to fetch repository items");
            _logger.LogError("   Repository: {RepositoryId}", repositoryId);
            _logger.LogError("   Branch: {Branch}", branch);
            _logger.LogError("   Error: {Message}", ex.Message);
            _logger.LogError("════════════════════════════════════════════════════════════");
            return new List<string>();
        }
    }
}

public class PullRequestCommentPostResult
{
    public bool Success { get; set; }
    public string Stage { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string? ResponseBody { get; set; }
    public int? ThreadId { get; set; }

    public static PullRequestCommentPostResult SuccessResult(int? threadId = null)
    {
        return new PullRequestCommentPostResult
        {
            Success = true,
            Stage = "success",
            ThreadId = threadId
        };
    }

    public static PullRequestCommentPostResult FailureResult(string stage, int? statusCode, string errorMessage, string? responseBody)
    {
        return new PullRequestCommentPostResult
        {
            Success = false,
            Stage = stage,
            StatusCode = statusCode,
            ErrorMessage = errorMessage,
            ResponseBody = responseBody
        };
    }
}

public class AgentThreadStatus
{
    public string CommentId { get; set; } = string.Empty;
    public int    ThreadId  { get; set; }
    /// <summary>active | fixed | wontFix | byDesign | closed | pending</summary>
    public string Status    { get; set; } = "active";
}
