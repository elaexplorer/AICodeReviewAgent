using CodeReviewAgent.Agents;
using CodeReviewAgent.Models;
using CodeReviewAgent.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeReviewAgent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CodeReviewController : ControllerBase
{
    private const string PullRequestFetchFailedClientError = "Internal server error while retrieving pull request from Azure DevOps.";
    private readonly CodeReviewAgentService _codeReviewAgent;
    private readonly AzureDevOpsMcpClient _adoClient;
    private readonly CodeReviewService _reviewService;
    private readonly AdoConfigurationService _adoConfig;
    private readonly ChatConfigurationService _chatConfig;
    private readonly EmbeddingConfigurationService _embeddingConfig;
    private readonly CodebaseContextService _codebaseContextService;
    private readonly CommentFeedbackService _feedbackService;
    private readonly ILogger<CodeReviewController> _logger;
    private static ReviewResult? _currentReview;
    private static readonly HashSet<string> _indexingInProgress = new();
    private static readonly object _indexingLock = new();
    private static readonly ConcurrentDictionary<string, ReviewByLinkAndPostJobStatus> _reviewByLinkAndPostJobs = new();
    private static readonly TimeSpan ReviewByLinkAndPostSyncWait = TimeSpan.FromSeconds(220);
    private static readonly HttpClient _localClaudeHttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    public CodeReviewController(
        CodeReviewAgentService codeReviewAgent,
        AzureDevOpsMcpClient adoClient,
        CodeReviewService reviewService,
        AdoConfigurationService adoConfig,
        ChatConfigurationService chatConfig,
        EmbeddingConfigurationService embeddingConfig,
        CodebaseContextService codebaseContextService,
        CommentFeedbackService feedbackService,
        ILogger<CodeReviewController> logger)
    {
        _codeReviewAgent = codeReviewAgent;
        _adoClient = adoClient;
        _reviewService = reviewService;
        _adoConfig = adoConfig;
        _chatConfig = chatConfig;
        _embeddingConfig = embeddingConfig;
        _codebaseContextService = codebaseContextService;
        _feedbackService = feedbackService;
        _logger = logger;
    }

    [HttpPost("start")]
    public async Task<IActionResult> StartReview([FromBody] ReviewRequest request)
    {
        try
        {
            _logger.LogInformation("Starting review for PR {PullRequestId} in {Repository}",
                request.PullRequestId, request.Repository);

            // Fetch PR details
            var pullRequest = await _adoClient.GetPullRequestAsync(
                request.Project, request.Repository, request.PullRequestId);

            if (pullRequest == null)
            {
                _logger.LogError(
                    "Pull request fetch returned null for PR {PullRequestId} in {Project}/{Repository}. ADO config: IsConfigured={IsConfigured}, Organization={Organization}, ForceUiConfig={ForceUiConfig}, HasDefaultPat={HasDefaultPat}",
                    request.PullRequestId,
                    request.Project,
                    request.Repository,
                    _adoConfig.IsConfigured,
                    _adoConfig.Organization,
                    IsForceUiConfigEnabled(),
                    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ADO_PAT")));

                return StatusCode(500, new
                {
                    error = PullRequestFetchFailedClientError,
                    errorCode = "ADO_PR_FETCH_FAILED"
                });
            }

            TriggerBackgroundIndexing(
                request.Project,
                request.Repository,
                GetBranchName(pullRequest.TargetBranch),
                GetOptionalAdoAccessTokenHeader());

            // Get PR files
            var files = await _adoClient.GetPullRequestFilesAsync(
                request.Project, request.Repository, request.PullRequestId);

            // Perform code review
            var comments = await _reviewService.ReviewPullRequestAsync(
                pullRequest, files, request.Project, request.Repository);

            // Store the review result
            _currentReview = new ReviewResult
            {
                PullRequest = pullRequest,
                Files = files,
                Comments = comments,
                Project = request.Project,
                Repository = request.Repository
            };

            return Ok(_currentReview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting review");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("review-by-link")]
    public async Task<IActionResult> ReviewByPullRequestLink([FromBody] ReviewByLinkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PullRequestLink))
        {
            return BadRequest(new { error = "pullRequestLink is required" });
        }

        if (!TryParseAzureDevOpsPullRequestLink(request.PullRequestLink, out var parsed, out var parseError))
        {
            return BadRequest(new { error = parseError });
        }

        if (!string.IsNullOrWhiteSpace(_adoConfig.Organization) &&
            !string.IsNullOrWhiteSpace(parsed.Organization) &&
            !string.Equals(_adoConfig.Organization, parsed.Organization, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                error = $"PR link organization '{parsed.Organization}' does not match configured organization '{_adoConfig.Organization}'."
            });
        }

        try
        {
            var adoAccessTokenOverride = GetOptionalAdoAccessTokenHeader();

            _logger.LogInformation("Starting review from link for PR {PullRequestId} in {Project}/{Repository}",
                parsed.PullRequestId, parsed.Project, parsed.Repository);

            var pullRequest = await _adoClient.GetPullRequestAsync(
                parsed.Project, parsed.Repository, parsed.PullRequestId, adoAccessTokenOverride);

            if (pullRequest == null)
            {
                _logger.LogError(
                    "Pull request fetch returned null for PR {PullRequestId} in {Project}/{Repository}. ADO config: IsConfigured={IsConfigured}, Organization={Organization}, ForceUiConfig={ForceUiConfig}, HasDefaultPat={HasDefaultPat}",
                    parsed.PullRequestId,
                    parsed.Project,
                    parsed.Repository,
                    _adoConfig.IsConfigured,
                    _adoConfig.Organization,
                    IsForceUiConfigEnabled(),
                    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ADO_PAT")));

                return StatusCode(500, new
                {
                    error = PullRequestFetchFailedClientError,
                    errorCode = "ADO_PR_FETCH_FAILED"
                });
            }

            TriggerBackgroundIndexing(
                parsed.Project,
                parsed.Repository,
                GetBranchName(pullRequest.TargetBranch),
                adoAccessTokenOverride);

            var files = await _adoClient.GetPullRequestFilesAsync(
                parsed.Project, parsed.Repository, parsed.PullRequestId);

            var comments = await _reviewService.ReviewPullRequestAsync(
                pullRequest, files, parsed.Project, parsed.Repository);

            _currentReview = new ReviewResult
            {
                PullRequest = pullRequest,
                Files = files,
                Comments = comments,
                Project = parsed.Project,
                Repository = parsed.Repository
            };

            return Ok(new
            {
                pullRequestLink = request.PullRequestLink,
                project = parsed.Project,
                repository = parsed.Repository,
                pullRequestId = parsed.PullRequestId,
                comments = comments
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running review from PR link");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("review-by-link-and-post")]
    public async Task<IActionResult> ReviewByPullRequestLinkAndPost([FromBody] ReviewByLinkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PullRequestLink))
        {
            return BadRequest(new { error = "pullRequestLink is required" });
        }

        if (!TryParseAndValidatePullRequestLink(request.PullRequestLink, out var parsed, out var errorResult))
        {
            return errorResult!;
        }

        var adoAccessTokenOverride = GetOptionalAdoAccessTokenHeader();
        var localClaudeUrl = Environment.GetEnvironmentVariable("LOCAL_CLAUDE_AGENT_URL");

        var jobId = Guid.NewGuid().ToString("N");
        _reviewByLinkAndPostJobs[jobId] = new ReviewByLinkAndPostJobStatus
        {
            JobId = jobId,
            PullRequestLink = request.PullRequestLink,
            Project = parsed.Project,
            Repository = parsed.Repository,
            PullRequestId = parsed.PullRequestId,
            Status = "running",
            StartedAtUtc = DateTime.UtcNow
        };

        var jobStartedAt = DateTime.UtcNow;
        var backgroundTask = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation(
                    "Starting review-by-link-and-post job {JobId} for PR {PullRequestId} in {Project}/{Repository} (local Claude: {LocalClaude})",
                    jobId, parsed.PullRequestId, parsed.Project, parsed.Repository,
                    string.IsNullOrWhiteSpace(localClaudeUrl) ? "disabled" : localClaudeUrl);

                // Run GPT-4 and local Claude in parallel
                var gptTask    = RunReviewByLinkCoreAsync(request.PullRequestLink, parsed, adoAccessTokenOverride);
                var claudeTask = string.IsNullOrWhiteSpace(localClaudeUrl)
                    ? Task.FromResult<List<CodeReviewComment>>([])
                    : CallLocalClaudeAgentAsync(localClaudeUrl, request.PullRequestLink, adoAccessTokenOverride);

                await Task.WhenAll(gptTask, claudeTask);

                var reviewOutput    = await gptTask;
                var claudeComments  = await claudeTask;

                _logger.LogInformation(
                    "Parallel review complete — GPT-4: {GptCount} comments, Claude: {ClaudeCount} comments",
                    reviewOutput.Comments.Count, claudeComments.Count);

                // Post high/critical comments from both models (union), deduped by position
                var highPriorityComments = reviewOutput.Comments
                    .Where(IsHighPriorityComment)
                    .Concat(claudeComments.Where(IsHighPriorityComment))
                    .GroupBy(c => $"{NormalizePath(c.FilePath)}:{c.StartLine}")
                    .Select(g => g.First())
                    .ToList();

                _logger.LogInformation(
                    "Union filter: {GptHigh} GPT-4 high/critical + {ClaudeHigh} Claude high/critical → {Total} to post (after dedup)",
                    reviewOutput.Comments.Count(IsHighPriorityComment),
                    claudeComments.Count(IsHighPriorityComment),
                    highPriorityComments.Count);
                var validRightSideLinesByFile = BuildValidRightSideLineLookup(reviewOutput.Files);
                var existingFingerprints = await _adoClient.GetExistingCommentFingerprintsAsync(
                    reviewOutput.Project,
                    reviewOutput.Repository,
                    reviewOutput.PullRequestId);
                var existingPositionKeys = await _adoClient.GetExistingCommentPositionKeysAsync(
                    reviewOutput.Project,
                    reviewOutput.Repository,
                    reviewOutput.PullRequestId);

                var postedCount = 0;
                var skippedCount = 0;
                var postedComments  = new List<CodeReviewComment>();
                var postingFailures = new List<ReviewPostingFailure>();
                foreach (var comment in highPriorityComments)
                {
                    var commentToPost = CloneComment(comment);
                    var remappedLine = RemapToNearestValidRightSideLine(commentToPost, validRightSideLinesByFile);
                    if (remappedLine.HasValue)
                    {
                        _logger.LogInformation(
                            "Adjusted comment line anchor for PR {PullRequestId} at {FilePath} from {OriginalLine} to {MappedLine}",
                            reviewOutput.PullRequestId,
                            commentToPost.FilePath,
                            comment.StartLine,
                            remappedLine.Value);
                    }

                    var fingerprint = AzureDevOpsRestClient.BuildCommentFingerprint(commentToPost);
                    var positionKey = AzureDevOpsRestClient.BuildCommentPositionKey(commentToPost);
                    if (existingFingerprints.Contains(fingerprint) || existingPositionKeys.Contains(positionKey))
                    {
                        skippedCount++;
                        comment.Posted = true;
                        comment.StartLine = commentToPost.StartLine;
                        comment.EndLine = commentToPost.EndLine;
                        _logger.LogInformation(
                            "Skipping duplicate high-priority comment for PR {PullRequestId} at {FilePath}:{StartLine}",
                            reviewOutput.PullRequestId,
                            commentToPost.FilePath,
                            commentToPost.StartLine);
                        continue;
                    }

                    var postResult = await _adoClient.PostCommentWithResultAsync(
                        reviewOutput.Project,
                        reviewOutput.Repository,
                        reviewOutput.PullRequestId,
                        commentToPost,
                        adoAccessTokenOverride);

                    if (postResult.Success)
                    {
                        comment.Posted = true;
                        comment.StartLine = commentToPost.StartLine;
                        comment.EndLine = commentToPost.EndLine;
                        postedCount++;
                        postedComments.Add(commentToPost);
                        existingFingerprints.Add(fingerprint);
                        existingPositionKeys.Add(positionKey);
                    }
                    else
                    {
                        postingFailures.Add(new ReviewPostingFailure
                        {
                            CommentId = comment.Id,
                            FilePath = commentToPost.FilePath,
                            StartLine = commentToPost.StartLine,
                            EndLine = commentToPost.EndLine,
                            Severity = comment.Severity,
                            Stage = postResult.Stage,
                            StatusCode = postResult.StatusCode,
                            ErrorMessage = postResult.ErrorMessage
                        });

                        _logger.LogWarning(
                            "Failed to post high-priority comment for PR {PullRequestId} at {FilePath}:{StartLine}. Stage={Stage}, StatusCode={StatusCode}, Error={Error}",
                            reviewOutput.PullRequestId,
                            commentToPost.FilePath,
                            commentToPost.StartLine,
                            postResult.Stage,
                            postResult.StatusCode,
                            postResult.ErrorMessage);
                    }
                }

                // Send email summary — fire and forget, don't block job completion
                _ = SendReviewEmailAsync(
                    request.PullRequestLink,
                    reviewOutput.PullRequestId,
                    reviewOutput.Project,
                    reviewOutput.Repository,
                    gptComments:        reviewOutput.Comments,
                    claudeComments:     claudeComments,
                    filteredToPost:     highPriorityComments.Count,
                    postedComments:     postedComments,
                    skippedCount:       skippedCount,
                    jobStartedAt:       jobStartedAt,
                    jobCompletedAt:     DateTime.UtcNow,
                    _logger);

                _reviewByLinkAndPostJobs[jobId] = new ReviewByLinkAndPostJobStatus
                {
                    JobId = jobId,
                    PullRequestLink = request.PullRequestLink,
                    Project = reviewOutput.Project,
                    Repository = reviewOutput.Repository,
                    PullRequestId = reviewOutput.PullRequestId,
                    Status = "completed",
                    StartedAtUtc = _reviewByLinkAndPostJobs[jobId].StartedAtUtc,
                    CompletedAtUtc = DateTime.UtcNow,
                    TotalComments = reviewOutput.Comments.Count,
                    HighPriorityComments = highPriorityComments.Count,
                    PostedHighPriorityComments = postedCount,
                    SkippedHighPriorityComments = skippedCount,
                    Comments = reviewOutput.Comments,
                    PostingFailures = postingFailures
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "review-by-link-and-post job {JobId} failed", jobId);

                var existing = _reviewByLinkAndPostJobs[jobId];
                existing.Status = "failed";
                existing.Error = PullRequestFetchFailedClientError;
                existing.CompletedAtUtc = DateTime.UtcNow;
                _reviewByLinkAndPostJobs[jobId] = existing;
            }
        });

        var completedTask = await Task.WhenAny(backgroundTask, Task.Delay(ReviewByLinkAndPostSyncWait));
        if (completedTask == backgroundTask)
        {
            var finalState = _reviewByLinkAndPostJobs[jobId];
            if (string.Equals(finalState.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(500, new
                {
                    error = finalState.Error ?? PullRequestFetchFailedClientError,
                    errorCode = "INTERNAL_SERVER_ERROR",
                    jobId
                });
            }

            return Ok(new
            {
                jobId,
                pullRequestLink = finalState.PullRequestLink,
                project = finalState.Project,
                repository = finalState.Repository,
                pullRequestId = finalState.PullRequestId,
                comments = finalState.Comments,
                posting = new
                {
                    highPriorityComments = finalState.HighPriorityComments,
                    postedHighPriorityComments = finalState.PostedHighPriorityComments,
                    skippedHighPriorityComments = finalState.SkippedHighPriorityComments,
                    failures = finalState.PostingFailures
                },
                status = finalState.Status
            });
        }

        // If the request takes too long, continue processing in background and return immediately.
        return Accepted(new
        {
            jobId,
            status = "running",
            message = "Review continues in background and high-priority comments will be posted when ready, even if the client request times out.",
            project = parsed.Project,
            repository = parsed.Repository,
            pullRequestId = parsed.PullRequestId,
            pullRequestLink = request.PullRequestLink
        });
    }

    [HttpGet("current")]
    public IActionResult GetCurrentReview()
    {
        if (_currentReview == null)
        {
            return NotFound(new { error = "No active review" });
        }

        return Ok(_currentReview);
    }

    [HttpGet("projects")]
    public IActionResult GetProjects()
    {
        // Return predefined sample list of projects used by the UI dropdown.
        var projects = new List<ProjectInfo>
        {
            new ProjectInfo
            {
                Name = "SCC",
                DisplayName = "SCC",
                Repositories = new List<string> { "service-shared_framework_waimea" }
            },
            new ProjectInfo
            {
                Name = "MyProject",
                DisplayName = "My Project",
                Repositories = new List<string>()
            }
        };

        return Ok(projects);
    }

    [HttpGet("repositories/{project}")]
    public async Task<IActionResult> GetRepositories(string project)
    {
        try
        {
            // In a real implementation, fetch repositories from ADO.
            // For sample mode, return a small predefined map.
            var repositories = project switch
            {
                "SCC" => new List<string> { "service-shared_framework_waimea" },
                _ => new List<string>()
            };

            return Ok(repositories);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching repositories");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("pullrequests/{project}/{repository}")]
    public async Task<IActionResult> GetActivePullRequests(string project, string repository)
    {
        try
        {
            // Check if configuration is valid
            if (!_adoConfig.IsConfigured)
            {
                return Unauthorized(new {
                    error = "Azure DevOps not configured. Please provide your Personal Access Token.",
                    requiresConfig = true
                });
            }

            _logger.LogInformation("Fetching active PRs for {Project}/{Repository}", project, repository);

            // Get active pull requests from ADO
            var prs = await _adoClient.GetActivePullRequestsAsync(project, repository);

            // Trigger background indexing if not already indexed or in progress
            TriggerBackgroundIndexing(project, repository);

            // Return index status along with PRs
            var isIndexed = _codebaseContextService.IsRepositoryIndexed(repository);
            var chunkCount = _codebaseContextService.GetChunkCount(repository);
            bool isCurrentlyIndexing;
            lock (_indexingLock)
            {
                isCurrentlyIndexing = _indexingInProgress.Contains(repository);
            }

            return Ok(new {
                pullRequests = prs,
                indexStatus = new {
                    isIndexed = isIndexed,
                    isIndexing = isCurrentlyIndexing,
                    chunkCount = chunkCount
                }
            });
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("401") || ex.Message.Contains("Unauthorized"))
        {
            _logger.LogError(ex, "Authentication error fetching pull requests");
            return Unauthorized(new {
                error = "Authentication failed. Your Personal Access Token may have expired or doesn't have access to this organization/project.",
                requiresConfig = true
            });
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404") || ex.Message.Contains("Not Found"))
        {
            _logger.LogError(ex, "Project or repository not found");
            return NotFound(new {
                error = $"Project '{project}' or repository '{repository}' not found."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching pull requests");
            return StatusCode(500, new { error = $"Failed to fetch pull requests: {ex.Message}" });
        }
    }

    /// <summary>
    /// Triggers background indexing for a repository if not already indexed or in progress
    /// </summary>
    private void TriggerBackgroundIndexing(
        string project,
        string repository,
        string branch = "master",
        string? accessTokenOverride = null)
    {
        // Skip if already indexed
        if (_codebaseContextService.IsRepositoryIndexed(repository))
        {
            _logger.LogInformation("📦 Repository '{Repository}' is already indexed ({ChunkCount} chunks)",
                repository, _codebaseContextService.GetChunkCount(repository));
            return;
        }

        // Skip if indexing is already in progress
        lock (_indexingLock)
        {
            if (_indexingInProgress.Contains(repository))
            {
                _logger.LogInformation("⏳ Indexing already in progress for '{Repository}'", repository);
                return;
            }
            _indexingInProgress.Add(repository);
        }

        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║ AUTO-INDEX: Starting Background Indexing                   ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
        _logger.LogInformation("Repository: {Repository}", repository);
        _logger.LogInformation("Indexing will run in background while you browse PRs...");

        // Run indexing in background (fire and forget)
        _ = Task.Run(async () =>
        {
            try
            {
                var startTime = DateTime.UtcNow;
                _logger.LogInformation("🚀 Background indexing started for '{Repository}'", repository);

                var chunksIndexed = await _codebaseContextService.IndexRepositoryAsync(project, repository, branch, accessTokenOverride);

                var duration = DateTime.UtcNow - startTime;
                _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
                _logger.LogInformation("║ AUTO-INDEX: Background Indexing Complete                   ║");
                _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");

                if (chunksIndexed > 0)
                {
                    _logger.LogInformation("✅ Repository '{Repository}' indexed successfully", repository);
                    _logger.LogInformation("   Chunks indexed: {ChunkCount}", chunksIndexed);
                    _logger.LogInformation("   Duration: {Duration:F1} seconds", duration.TotalSeconds);
                    _logger.LogInformation("   Embeddings are now ready for semantic search!");
                }
                else
                {
                    _logger.LogWarning("⚠️ Repository '{Repository}' indexing completed with 0 chunks", repository);
                    _logger.LogWarning("   Duration: {Duration:F1} seconds", duration.TotalSeconds);
                    _logger.LogWarning("   Check embedding service configuration (endpoint/key/deployment) and ADO clone access.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Background indexing failed for '{Repository}'", repository);
            }
            finally
            {
                lock (_indexingLock)
                {
                    _indexingInProgress.Remove(repository);
                }
            }
        });
    }

    private static string GetBranchName(string? fullRef)
    {
        if (string.IsNullOrWhiteSpace(fullRef))
        {
            return "master";
        }

        const string headsPrefix = "refs/heads/";
        if (fullRef.StartsWith(headsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return fullRef.Substring(headsPrefix.Length);
        }

        return fullRef;
    }

    /// <summary>
    /// Accepts a pre-computed list of review comments (e.g. from a local Claude review),
    /// deduplicates against comments already posted on the PR, and posts only the net-new ones.
    /// Designed for the daily local script that consolidates multi-model reviews before posting.
    /// </summary>
    [HttpPost("comments/post")]
    public async Task<IActionResult> PostConsolidatedComments([FromBody] PostConsolidatedCommentsRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Project) ||
                string.IsNullOrWhiteSpace(request.Repository) ||
                request.PullRequestId <= 0 ||
                request.Comments == null || request.Comments.Count == 0)
            {
                return BadRequest(new { error = "project, repository, pullRequestId and at least one comment are required" });
            }

            // Use the token from the header if present (caller passes bot PAT here),
            // otherwise fall back to the server's configured personal PAT.
            var postingToken = GetOptionalAdoAccessTokenHeader();

            _logger.LogInformation(
                "POST comments/post: {Count} candidate comments for PR {PrId} in {Project}/{Repository}",
                request.Comments.Count, request.PullRequestId, request.Project, request.Repository);

            // Fetch PR files so we can remap comment lines to valid diff positions
            var files = await _adoClient.GetPullRequestFilesAsync(
                request.Project, request.Repository, request.PullRequestId);

            var validRightSideLinesByFile = BuildValidRightSideLineLookup(files);

            // Fetch fingerprints + position keys already on the PR
            var existingFingerprints = await _adoClient.GetExistingCommentFingerprintsAsync(
                request.Project, request.Repository, request.PullRequestId);

            var existingPositionKeys = await _adoClient.GetExistingCommentPositionKeysAsync(
                request.Project, request.Repository, request.PullRequestId);

            var postedCount  = 0;
            var skippedCount = 0;
            var failedCount  = 0;
            var postedComments  = new List<object>();
            var skippedComments = new List<object>();

            foreach (var incoming in request.Comments)
            {
                // Only post critical/high severity — guard against stale clients sending medium/low
                var incomingSeverity = (incoming.Severity ?? "medium").Trim().ToLowerInvariant();
                if (incomingSeverity is not ("critical" or "high"))
                {
                    _logger.LogInformation(
                        "Skipping non-critical comment at {FilePath}:{Line} (severity={Severity})",
                        incoming.FilePath, incoming.StartLine, incomingSeverity);
                    skippedCount++;
                    skippedComments.Add(new { incoming.FilePath, incoming.StartLine, reason = $"severity={incomingSeverity}" });
                    continue;
                }

                // Map to CodeReviewComment
                var comment = new CodeReviewComment
                {
                    Id          = Guid.NewGuid().ToString(),
                    FilePath    = incoming.FilePath,
                    StartLine   = incoming.StartLine,
                    EndLine     = incoming.EndLine > 0 ? incoming.EndLine : incoming.StartLine,
                    CommentText = incoming.CommentText,
                    CommentType = incoming.CommentType ?? "issue",
                    Severity    = incoming.Severity    ?? "medium",
                    SuggestedFix = incoming.SuggestedFix ?? string.Empty,
                    Confidence  = incoming.Confidence
                };

                RemapToNearestValidRightSideLine(comment, validRightSideLinesByFile);

                var fingerprint = AzureDevOpsRestClient.BuildCommentFingerprint(comment);
                var positionKey = AzureDevOpsRestClient.BuildCommentPositionKey(comment);

                if (existingFingerprints.Contains(fingerprint) || existingPositionKeys.Contains(positionKey))
                {
                    skippedCount++;
                    skippedComments.Add(new { comment.FilePath, comment.StartLine, reason = "duplicate" });
                    _logger.LogInformation(
                        "Skipping duplicate comment at {FilePath}:{Line}", comment.FilePath, comment.StartLine);
                    continue;
                }

                var postResult = await _adoClient.PostCommentWithResultAsync(
                    request.Project, request.Repository, request.PullRequestId,
                    comment, postingToken);

                if (postResult.Success)
                {
                    postedCount++;
                    existingFingerprints.Add(fingerprint);
                    existingPositionKeys.Add(positionKey);
                    postedComments.Add(new { comment.FilePath, comment.StartLine, comment.Severity });
                    _logger.LogInformation(
                        "Posted comment at {FilePath}:{Line} severity={Severity}",
                        comment.FilePath, comment.StartLine, comment.Severity);
                }
                else
                {
                    failedCount++;
                    _logger.LogWarning(
                        "Failed to post comment at {FilePath}:{Line} — {Error}",
                        comment.FilePath, comment.StartLine, postResult.ErrorMessage);
                }
            }

            _logger.LogInformation(
                "POST comments/post complete: posted={Posted} skipped={Skipped} failed={Failed}",
                postedCount, skippedCount, failedCount);

            return Ok(new
            {
                posted   = postedCount,
                skipped  = skippedCount,
                failed   = failedCount,
                postedComments,
                skippedComments
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in POST comments/post");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("comment")]
    public async Task<IActionResult> PostComment([FromBody] PostCommentRequest request)
    {
        try
        {
            if (_currentReview == null)
            {
                return BadRequest(new { error = "No active review" });
            }

            var comment = _currentReview.Comments.FirstOrDefault(c => c.Id == request.CommentId);
            if (comment == null)
            {
                return NotFound(new { error = "Comment not found" });
            }

            var success = await _adoClient.PostCommentAsync(
                _currentReview.Project,
                _currentReview.Repository,
                _currentReview.PullRequest.Id,
                comment);

            if (success)
            {
                // Mark comment as posted
                comment.Posted = true;
                return Ok(new { success = true, message = "Comment posted successfully" });
            }
            else
            {
                return StatusCode(500, new { error = "Failed to post comment" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting comment");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("config/status")]
    public IActionResult GetConfigStatus()
    {
        var forceUiConfig = IsForceUiConfigEnabled();
        var hasDefaultPat = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ADO_PAT"));
        var hasDefaultChatApiKey =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"));
        var hasDefaultEmbeddingApiKey =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_API_KEY")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"));
        
        return Ok(new {
            isConfigured = _adoConfig.IsConfigured,
            organization = _adoConfig.Organization,
            forceUiConfig = forceUiConfig,
            hasDefaultPat = forceUiConfig ? false : hasDefaultPat,
            chatIsConfigured = _chatConfig.IsConfigured,
            chatEndpoint = _chatConfig.Endpoint,
            chatDeployment = _chatConfig.Deployment,
            chatApiVersion = _chatConfig.ApiVersion,
            hasDefaultChatApiKey = forceUiConfig ? false : hasDefaultChatApiKey,
            embeddingIsConfigured = _embeddingConfig.IsConfigured,
            embeddingEndpoint = _embeddingConfig.Endpoint,
            embeddingDeployment = _embeddingConfig.Deployment,
            embeddingApiVersion = _embeddingConfig.ApiVersion,
            hasDefaultEmbeddingApiKey = forceUiConfig ? false : hasDefaultEmbeddingApiKey
        });
    }

    private static bool IsForceUiConfigEnabled()
    {
        var value = Environment.GetEnvironmentVariable("FORCE_UI_CONFIG");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    [HttpPost("config/validate")]
    public async Task<IActionResult> ValidateConfig([FromBody] ConfigRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.PersonalAccessToken))
            {
                return BadRequest(new {
                    success = false,
                    error = "Personal Access Token is required."
                });
            }

            var organization = Environment.GetEnvironmentVariable("ADO_ORGANIZATION")?.Trim();
            if (string.IsNullOrWhiteSpace(organization))
            {
                return BadRequest(new {
                    success = false,
                    error = "ADO_ORGANIZATION is not configured in environment."
                });
            }

            var (isValid, errorMessage) = await _adoConfig.ValidateAndConfigureAsync(organization, request.PersonalAccessToken);

            if (!isValid)
            {
                return BadRequest(new {
                    success = false,
                    error = errorMessage
                });
            }

            if (!_chatConfig.IsConfigured)
            {
                return BadRequest(new {
                    success = false,
                    error = "Chat configuration is missing in environment. Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT."
                });
            }

            if (!_embeddingConfig.IsConfigured)
            {
                return BadRequest(new {
                    success = false,
                    error = "Embedding configuration is missing in environment. Set embedding endpoint/key/deployment variables."
                });
            }

            // Reinitialize ADO client with new credentials
            _adoClient.UpdateConfiguration(organization, request.PersonalAccessToken);

            return Ok(new {
                success = true,
                message = "Configuration validated successfully",
                organization = organization
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating configuration");
            return StatusCode(500, new {
                success = false,
                error = $"Validation error: {ex.Message}"
            });
        }
    }

    [HttpPost("config/clear")]
    public IActionResult ClearConfig()
    {
        _adoConfig.ClearConfiguration();
        _chatConfig.ClearConfiguration();
        _embeddingConfig.ClearConfiguration();
        return Ok(new { success = true, message = "Configuration cleared" });
    }

    /// <summary>
    /// Index a repository for RAG-based context retrieval
    /// This generates embeddings for all code files and stores them for semantic search
    /// </summary>
    [HttpPost("index")]
    public async Task<IActionResult> IndexRepository([FromBody] IndexRequest request)
    {
        try
        {
            _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║ API: Starting Repository Indexing                          ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("Project: {Project}", request.Project);
            _logger.LogInformation("Repository: {Repository}", request.Repository);
            _logger.LogInformation("Branch: {Branch}", request.Branch);

            var indexedCount = await _codebaseContextService.IndexRepositoryAsync(
                request.Project,
                request.Repository,
                request.Branch,
                GetOptionalAdoAccessTokenHeader());

            _logger.LogInformation("✅ Indexing complete: {Count} chunks indexed", indexedCount);

            return Ok(new
            {
                success = true,
                message = $"Repository indexed successfully",
                chunksIndexed = indexedCount,
                repositoryId = request.Repository
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error indexing repository");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Incrementally refresh the RAG index for a repository (re-embed only changed files).
    /// </summary>
    [HttpPost("index/refresh")]
    public async Task<IActionResult> RefreshIndex([FromBody] IndexRequest request)
    {
        try
        {
            _logger.LogInformation("🔄 API: Incremental refresh requested for '{Repository}'", request.Repository);

            var updatedChunks = await _codebaseContextService.RefreshIndexAsync(
                request.Project,
                request.Repository,
                request.Branch,
                GetOptionalAdoAccessTokenHeader());

            var message = updatedChunks switch
            {
                0  => "Index is already up-to-date",
                -1 => "Fell back to full re-index",
                _  => $"Refreshed {updatedChunks} chunks"
            };

            return Ok(new
            {
                success = true,
                message,
                chunksUpdated = updatedChunks,
                totalChunks = _codebaseContextService.GetChunkCount(request.Repository),
                repositoryId = request.Repository
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error refreshing index for '{Repository}'", request.Repository);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get RAG indexing status for a repository
    /// </summary>
    [HttpGet("index/status/{repositoryId}")]
    public IActionResult GetIndexStatus(string repositoryId)
    {
        var isIndexed = _codebaseContextService.IsRepositoryIndexed(repositoryId);
        var chunkCount = _codebaseContextService.GetChunkCount(repositoryId);

        return Ok(new
        {
            repositoryId = repositoryId,
            isIndexed = isIndexed,
            chunkCount = chunkCount
        });
    }

    private static bool TryParseAzureDevOpsPullRequestLink(
        string pullRequestLink,
        out ParsedPullRequestLink parsed,
        out string error)
    {
        parsed = new ParsedPullRequestLink();
        error = string.Empty;

        if (!Uri.TryCreate(pullRequestLink, UriKind.Absolute, out var uri))
        {
            error = "Invalid pullRequestLink. Provide a full Azure DevOps PR URL.";
            return false;
        }

        var pathSegments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        var prIdMatch = Regex.Match(uri.AbsolutePath, @"/pullrequest/(\d+)", RegexOptions.IgnoreCase);
        if (!prIdMatch.Success || !int.TryParse(prIdMatch.Groups[1].Value, out var prId))
        {
            error = "Could not extract pull request id from URL.";
            return false;
        }

        string organization = string.Empty;
        string project = string.Empty;
        string repository = string.Empty;

        if (uri.Host.EndsWith("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            // Format: /{organization}/{project}/_git/{repository}/pullrequest/{id}
            if (pathSegments.Length < 6)
            {
                error = "Unsupported Azure DevOps URL format.";
                return false;
            }

            organization = Uri.UnescapeDataString(pathSegments[0]);
            project = Uri.UnescapeDataString(pathSegments[1]);

            var gitIndex = Array.FindIndex(pathSegments, s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));
            if (gitIndex < 0 || gitIndex + 1 >= pathSegments.Length)
            {
                error = "Could not extract repository from URL.";
                return false;
            }

            repository = Uri.UnescapeDataString(pathSegments[gitIndex + 1]);
        }
        else if (uri.Host.EndsWith("visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            // Format: https://{organization}.visualstudio.com/{project}/_git/{repository}/pullrequest/{id}
            organization = uri.Host.Split('.')[0];
            if (pathSegments.Length < 5)
            {
                error = "Unsupported Visual Studio URL format.";
                return false;
            }

            project = Uri.UnescapeDataString(pathSegments[0]);

            var gitIndex = Array.FindIndex(pathSegments, s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));
            if (gitIndex < 0 || gitIndex + 1 >= pathSegments.Length)
            {
                error = "Could not extract repository from URL.";
                return false;
            }

            repository = Uri.UnescapeDataString(pathSegments[gitIndex + 1]);
        }
        else
        {
            error = "Only Azure DevOps pull request URLs are supported.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(repository))
        {
            error = "Could not parse project/repository from URL.";
            return false;
        }

        parsed = new ParsedPullRequestLink
        {
            Organization = organization,
            Project = project,
            Repository = repository,
            PullRequestId = prId
        };

        return true;
    }

    private bool TryParseAndValidatePullRequestLink(
        string pullRequestLink,
        out ParsedPullRequestLink parsed,
        out IActionResult? errorResult)
    {
        errorResult = null;

        if (!TryParseAzureDevOpsPullRequestLink(pullRequestLink, out parsed, out var parseError))
        {
            errorResult = BadRequest(new { error = parseError });
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_adoConfig.Organization) &&
            !string.IsNullOrWhiteSpace(parsed.Organization) &&
            !string.Equals(_adoConfig.Organization, parsed.Organization, StringComparison.OrdinalIgnoreCase))
        {
            errorResult = BadRequest(new
            {
                error = $"PR link organization '{parsed.Organization}' does not match configured organization '{_adoConfig.Organization}'."
            });
            return false;
        }

        return true;
    }

    private async Task<ReviewByLinkExecutionResult> RunReviewByLinkCoreAsync(
        string pullRequestLink,
        ParsedPullRequestLink parsed,
        string? accessTokenOverride = null)
    {
        var pullRequest = await _adoClient.GetPullRequestAsync(
            parsed.Project,
            parsed.Repository,
            parsed.PullRequestId,
            accessTokenOverride);

        if (pullRequest == null)
        {
            _logger.LogError(
                "RunReviewByLinkCoreAsync failed to retrieve PR {PullRequestId} in {Project}/{Repository}. ADO config: IsConfigured={IsConfigured}, Organization={Organization}, ForceUiConfig={ForceUiConfig}, HasDefaultPat={HasDefaultPat}",
                parsed.PullRequestId,
                parsed.Project,
                parsed.Repository,
                _adoConfig.IsConfigured,
                _adoConfig.Organization,
                IsForceUiConfigEnabled(),
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ADO_PAT")));

            throw new InvalidOperationException(PullRequestFetchFailedClientError);
        }

        TriggerBackgroundIndexing(
            parsed.Project,
            parsed.Repository,
            GetBranchName(pullRequest.TargetBranch),
            accessTokenOverride);

        var files = await _adoClient.GetPullRequestFilesAsync(
            parsed.Project,
            parsed.Repository,
            parsed.PullRequestId);

        var comments = await _reviewService.ReviewPullRequestAsync(
            pullRequest,
            files,
            parsed.Project,
            parsed.Repository);

        _currentReview = new ReviewResult
        {
            PullRequest = pullRequest,
            Files = files,
            Comments = comments,
            Project = parsed.Project,
            Repository = parsed.Repository
        };

        return new ReviewByLinkExecutionResult
        {
            PullRequestLink = pullRequestLink,
            Project = parsed.Project,
            Repository = parsed.Repository,
            PullRequestId = parsed.PullRequestId,
            Files = files,
            Comments = comments
        };
    }

    /// <summary>
    /// Sends a review summary email via Microsoft Graph API (avoids SMTP AUTH, which is
    /// disabled in most Microsoft 365 tenants).
    ///
    /// Sends a review summary email via Azure Logic App HTTP trigger.
    ///
    /// Required env vars:
    ///   REPORT_EMAIL_TO           — recipient(s), comma-separated
    ///   REPORT_LOGIC_APP_URL      — Logic App HTTP trigger URL (includes SAS signature)
    /// </summary>
    private static async Task SendReviewEmailAsync(
        string prLink,
        int prId,
        string project,
        string repository,
        List<CodeReviewComment> gptComments,
        List<CodeReviewComment> claudeComments,
        int filteredToPost,
        List<CodeReviewComment> postedComments,
        int skippedCount,
        DateTime jobStartedAt,
        DateTime jobCompletedAt,
        ILogger logger)
    {
        var emailTo      = Environment.GetEnvironmentVariable("REPORT_EMAIL_TO")       ?? "";
        var logicAppUrl  = Environment.GetEnvironmentVariable("REPORT_LOGIC_APP_URL")  ?? "";

        if (string.IsNullOrWhiteSpace(emailTo) || string.IsNullOrWhiteSpace(logicAppUrl))
        {
            logger.LogInformation("Email skipped — REPORT_EMAIL_TO or REPORT_LOGIC_APP_URL not configured");
            return;
        }

        var subject = postedComments.Count > 0
            ? $"AI Code Review — PR #{prId} ({project}/{repository}) — {postedComments.Count} comments posted"
            : $"AI Code Review — PR #{prId} ({project}/{repository}) — no high/critical issues found";

        var html = BuildReviewEmailHtml(prLink, prId, project, repository,
                                        gptComments, claudeComments, filteredToPost,
                                        postedComments, skippedCount, jobStartedAt, jobCompletedAt);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var payload = JsonSerializer.Serialize(new { to = emailTo, subject, body = html });
            var resp = await http.PostAsync(logicAppUrl,
                new StringContent(payload, Encoding.UTF8, "application/json"));
            resp.EnsureSuccessStatusCode();
            logger.LogInformation("Review email sent (Logic App) to {Recipients} for PR #{PrId}", emailTo, prId);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to send review email for PR #{PrId}: {Error}", prId, ex.Message);
        }
    }

    private static string BuildReviewEmailHtml(
        string prLink,
        int prId,
        string project,
        string repository,
        List<CodeReviewComment> gptComments,
        List<CodeReviewComment> claudeComments,
        int filteredToPost,
        List<CodeReviewComment> postedComments,
        int skippedCount,
        DateTime jobStartedAt,
        DateTime jobCompletedAt)
    {
        var startedStr         = jobStartedAt.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
        var completedStr       = jobCompletedAt.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
        var durationSec        = (int)(jobCompletedAt - jobStartedAt).TotalSeconds;
        var durationStr        = durationSec >= 60 ? $"{durationSec / 60}m {durationSec % 60}s" : $"{durationSec}s";
        var dualModel          = claudeComments.Count > 0;
        var modelTag           = dualModel ? "GPT + Claude (union)" : "GPT only";
        var notPosted          = Math.Max(0, filteredToPost - postedComments.Count - skippedCount);
        var gptHighComments    = gptComments.Where(c => c.Severity?.ToLower() is "critical" or "high").OrderByDescending(c => c.Confidence).ToList();
        var claudeHighComments = claudeComments.Where(c => c.Severity?.ToLower() is "critical" or "high").OrderByDescending(c => c.Confidence).ToList();

        // Helper: build a table of comments
        static string CommentTable(List<CodeReviewComment> items, string accentColor)
        {
            if (items.Count == 0)
                return "<tr><td colspan='4' style='padding:8px;color:#6a737d;font-style:italic'>None</td></tr>";
            var sb = new StringBuilder();
            foreach (var c in items)
            {
                var sev  = c.Severity?.ToLower() is "high" or "critical" ? "CRITICAL" : (c.Severity?.ToUpper() ?? "MEDIUM");
                var conf = $"{c.Confidence * 100:F0}%";
                var file = System.Web.HttpUtility.HtmlEncode(
                    System.IO.Path.GetFileName(c.FilePath ?? ""));
                var text = System.Web.HttpUtility.HtmlEncode((c.CommentText ?? "").Length > 120
                    ? (c.CommentText!)[..120] + "…"
                    : c.CommentText ?? "");
                sb.Append($"""
                    <tr style="border-bottom:1px solid #eaecef">
                      <td style="padding:6px 8px;white-space:nowrap">
                        <span style="font-size:10px;font-weight:700;color:{accentColor};background:#ffeef0;padding:2px 5px;border-radius:3px">{sev}</span>
                      </td>
                      <td style="padding:6px 8px;font-size:11px;color:#586069;white-space:nowrap">{file}:{c.StartLine}</td>
                      <td style="padding:6px 8px;font-size:12px;color:#24292e">{text}</td>
                      <td style="padding:6px 8px;font-size:11px;color:#6a737d;white-space:nowrap;text-align:right">{conf}</td>
                    </tr>
                    """);
            }
            return sb.ToString();
        }

        // Helper: posted comment cards
        var postedHtml = new StringBuilder();
        if (postedComments.Count == 0)
        {
            postedHtml.Append("<div style='color:#6a737d;padding:8px 0;font-style:italic'>No comments were posted to the PR.</div>");
        }
        else
        {
            foreach (var c in postedComments)
            {
                var text = System.Web.HttpUtility.HtmlEncode(c.CommentText ?? "");
                var fix  = System.Web.HttpUtility.HtmlEncode(c.SuggestedFix ?? "");
                var fixHtml = string.IsNullOrWhiteSpace(fix) ? "" :
                    $"<div style='margin-top:5px;font-size:12px;color:#555'><b>Fix:</b> {fix}</div>";
                postedHtml.Append($"""
                    <div style="margin:8px 0;padding:10px 14px;border-left:3px solid #d73a49;background:#ffeef0;border-radius:0 4px 4px 0">
                      <div style="font-size:11px;font-weight:700;color:#d73a49;margin-bottom:3px">
                        {(c.Severity?.ToLower() is "high" or "critical" ? "CRITICAL" : (c.Severity?.ToUpper() ?? "HIGH"))} &nbsp;·&nbsp; {c.Confidence * 100:F0}% confidence
                      </div>
                      <div style="font-size:11px;color:#586069;margin-bottom:4px">
                        <code>{System.Web.HttpUtility.HtmlEncode(c.FilePath ?? "")}:{c.StartLine}</code>
                      </div>
                      <div style="font-size:13px;color:#24292e">{text}</div>
                      {fixHtml}
                    </div>
                    """);
            }
        }

        return $"""
            <!DOCTYPE html>
            <html>
            <head><meta charset="utf-8"><title>AI Code Review — PR #{prId}</title></head>
            <body style="font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;background:#f6f8fa;margin:0;padding:24px">
              <div style="max-width:860px;margin:0 auto;background:#fff;border:1px solid #e1e4e8;border-radius:8px;overflow:hidden">

                <!-- Header -->
                <div style="background:#24292e;padding:20px 24px">
                  <h1 style="color:#fff;margin:0;font-size:18px">AI Code Review Report</h1>
                  <div style="color:#8b949e;font-size:12px;margin-top:4px">{project}/{repository} &nbsp;·&nbsp; {modelTag}</div>
                </div>

                <!-- Stats row -->
                <div style="display:flex;border-bottom:1px solid #e1e4e8">
                  <div style="flex:1;padding:14px 16px;border-right:1px solid #e1e4e8;text-align:center">
                    <div style="font-size:22px;font-weight:700;color:#0366d6">{gptComments.Count}</div>
                    <div style="font-size:11px;color:#586069;margin-top:2px">GPT total</div>
                  </div>
                  <div style="flex:1;padding:14px 16px;border-right:1px solid #e1e4e8;text-align:center">
                    <div style="font-size:22px;font-weight:700;color:#6f42c1">{claudeComments.Count}</div>
                    <div style="font-size:11px;color:#586069;margin-top:2px">Claude total</div>
                  </div>
                  <div style="flex:1;padding:14px 16px;border-right:1px solid #e1e4e8;text-align:center">
                    <div style="font-size:22px;font-weight:700;color:#d73a49">{filteredToPost}</div>
                    <div style="font-size:11px;color:#586069;margin-top:2px">High/critical (both)</div>
                  </div>
                  <div style="flex:1;padding:14px 16px;border-right:1px solid #e1e4e8;text-align:center">
                    <div style="font-size:22px;font-weight:700;color:#28a745">{postedComments.Count}</div>
                    <div style="font-size:11px;color:#586069;margin-top:2px">Posted</div>
                  </div>
                  <div style="flex:1;padding:14px 16px;border-right:1px solid #e1e4e8;text-align:center">
                    <div style="font-size:22px;font-weight:700;color:#6a737d">{skippedCount}</div>
                    <div style="font-size:11px;color:#586069;margin-top:2px">Skipped (dup)</div>
                  </div>
                  <div style="flex:1;padding:14px 16px;text-align:center">
                    <div style="font-size:22px;font-weight:700;color:#e36209">{notPosted}</div>
                    <div style="font-size:11px;color:#586069;margin-top:2px">Post failures</div>
                  </div>
                </div>

                <!-- Timing + PR link -->
                <div style="padding:10px 24px;background:#f6f8fa;border-bottom:1px solid #e1e4e8;font-size:12px;color:#586069">
                  <b>Started:</b> {startedStr} &nbsp;·&nbsp; <b>Completed:</b> {completedStr} &nbsp;·&nbsp; <b>Duration:</b> {durationStr}
                </div>
                <div style="padding:12px 24px;border-bottom:1px solid #e1e4e8">
                  <a href="{prLink}" style="font-weight:600;color:#0366d6;text-decoration:none;font-size:14px">PR #{prId}: {project}/{repository}</a>
                </div>

                <!-- GPT high/critical table -->
                <div style="padding:16px 24px;border-bottom:1px solid #e1e4e8">
                  <div style="font-size:13px;font-weight:600;color:#0366d6;margin-bottom:8px">
                    GPT — High/Critical Comments ({gptHighComments.Count})
                  </div>
                  <table style="width:100%;border-collapse:collapse;font-size:12px">
                    <thead>
                      <tr style="background:#f6f8fa;border-bottom:2px solid #e1e4e8">
                        <th style="padding:6px 8px;text-align:left;color:#586069;font-weight:600">Sev</th>
                        <th style="padding:6px 8px;text-align:left;color:#586069;font-weight:600">File:Line</th>
                        <th style="padding:6px 8px;text-align:left;color:#586069;font-weight:600">Comment</th>
                        <th style="padding:6px 8px;text-align:right;color:#586069;font-weight:600">Conf</th>
                      </tr>
                    </thead>
                    <tbody>{CommentTable(gptHighComments, "#d73a49")}</tbody>
                  </table>
                </div>

                <!-- Claude high/critical table -->
                <div style="padding:16px 24px;border-bottom:1px solid #e1e4e8">
                  <div style="font-size:13px;font-weight:600;color:#6f42c1;margin-bottom:8px">
                    Claude — High/Critical Comments ({claudeHighComments.Count})
                  </div>
                  <table style="width:100%;border-collapse:collapse;font-size:12px">
                    <thead>
                      <tr style="background:#f6f8fa;border-bottom:2px solid #e1e4e8">
                        <th style="padding:6px 8px;text-align:left;color:#586069;font-weight:600">Sev</th>
                        <th style="padding:6px 8px;text-align:left;color:#586069;font-weight:600">File:Line</th>
                        <th style="padding:6px 8px;text-align:left;color:#586069;font-weight:600">Comment</th>
                        <th style="padding:6px 8px;text-align:right;color:#586069;font-weight:600">Conf</th>
                      </tr>
                    </thead>
                    <tbody>{CommentTable(claudeHighComments, "#6f42c1")}</tbody>
                  </table>
                </div>

                <!-- Posted to PR -->
                <div style="padding:16px 24px">
                  <div style="font-size:13px;font-weight:600;color:#24292e;margin-bottom:8px">
                    Posted to PR ({postedComments.Count})
                  </div>
                  {postedHtml}
                </div>

                <!-- Footer -->
                <div style="padding:12px 24px;background:#f6f8fa;border-top:1px solid #e1e4e8;font-size:11px;color:#586069">
                  Generated by AI Code Review Agent &nbsp;·&nbsp; {modelTag}
                </div>

              </div>
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Submits a review job to the local Claude server, then polls until done (up to 12 min).
    /// The server returns immediately with a jobId; the skill runs asynchronously in the background
    /// so the devtunnel proxy never hits its gateway timeout.
    /// </summary>
    private async Task<List<CodeReviewComment>> CallLocalClaudeAgentAsync(
        string localAgentUrl, string pullRequestLink, string? adoToken)
    {
        var baseUrl = localAgentUrl.TrimEnd('/');
        try
        {
            // Step 1 — submit job
            var payload = JsonSerializer.Serialize(new { pullRequestLink });
            var submitReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/claudeCodeReview")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(adoToken))
                submitReq.Headers.TryAddWithoutValidation("X-Ado-Access-Token", adoToken);

            var submitResp = await _localClaudeHttpClient.SendAsync(submitReq);
            if (!submitResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Local Claude agent returned {Status} on submit", submitResp.StatusCode);
                return [];
            }

            var submitJson = await submitResp.Content.ReadAsStringAsync();
            var submitDoc  = JsonDocument.Parse(submitJson);
            if (!submitDoc.RootElement.TryGetProperty("jobId", out var jobIdEl))
            {
                // Older server returning comments directly — parse inline
                return ParseClaudeComments(submitDoc.RootElement);
            }
            var jobId = jobIdEl.GetString() ?? "";
            _logger.LogInformation("Local Claude job submitted: {JobId}", jobId);

            // Step 2 — poll until done (max 12 min, 20-second intervals)
            var pollUrl     = $"{baseUrl}/claudeCodeReview/{jobId}";
            var deadline    = DateTime.UtcNow.AddMinutes(12);
            var pollInterval = TimeSpan.FromSeconds(20);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(pollInterval);

                HttpResponseMessage pollResp;
                try { pollResp = await _localClaudeHttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, pollUrl)); }
                catch (Exception ex) { _logger.LogWarning("Poll error: {E}", ex.Message); continue; }

                if (pollResp.StatusCode == System.Net.HttpStatusCode.Accepted)
                    continue; // still pending

                if (!pollResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Local Claude agent poll returned {Status}", pollResp.StatusCode);
                    return [];
                }

                var pollJson = await pollResp.Content.ReadAsStringAsync();
                var pollDoc  = JsonDocument.Parse(pollJson);
                var comments = ParseClaudeComments(pollDoc.RootElement);
                _logger.LogInformation("Local Claude agent returned {Count} comments", comments.Count);
                return comments;
            }

            _logger.LogWarning("Local Claude agent timed out after 12 min. Proceeding with GPT-4 only.");
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Local Claude agent call failed — {Error}. Proceeding with GPT-4 only.", ex.Message);
            return [];
        }
    }

    private static List<CodeReviewComment> ParseClaudeComments(JsonElement root)
    {
        if (!root.TryGetProperty("comments", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var comments = new List<CodeReviewComment>();
        foreach (var item in arr.EnumerateArray())
        {
            comments.Add(new CodeReviewComment
            {
                Id          = Guid.NewGuid().ToString(),
                FilePath    = item.TryGetProperty("filePath",    out var fp)   ? fp.GetString()   ?? "" : "",
                StartLine   = item.TryGetProperty("startLine",   out var sl)   ? sl.GetInt32()         : 1,
                EndLine     = item.TryGetProperty("endLine",     out var el)   ? el.GetInt32()         : 1,
                Severity    = item.TryGetProperty("severity",    out var sev)  ? sev.GetString()  ?? "medium" : "medium",
                CommentType = item.TryGetProperty("commentType", out var ct)   ? ct.GetString()   ?? "issue"  : "issue",
                CommentText = item.TryGetProperty("commentText", out var txt)  ? txt.GetString()  ?? "" : "",
                Confidence  = item.TryGetProperty("confidence",  out var conf) ? conf.GetDouble() : 0.8,
            });
        }
        return comments;
    }

    /// <summary>
    /// Returns GPT-4 comments that have a matching Claude comment on the same file
    /// within ±10 lines. If Claude returned nothing (unreachable), returns GPT-4 comments as-is.
    /// </summary>
    private static List<CodeReviewComment> IntersectWithClaudeComments(
        List<CodeReviewComment> gptComments,
        List<CodeReviewComment> claudeComments)
    {
        if (claudeComments.Count == 0)
            return gptComments;

        return gptComments
            .Where(gpt => claudeComments.Any(c =>
                NormalizePath(c.FilePath) == NormalizePath(gpt.FilePath) &&
                Math.Abs(c.StartLine - gpt.StartLine) <= 10))
            .ToList();
    }

    private static bool IsHighPriorityComment(CodeReviewComment comment)
    {
        if (comment.Confidence < 0.7)
            return false;
        var severity = comment.Severity?.Trim().ToLowerInvariant();
        return severity is "critical" or "high";
    }

    private static CodeReviewComment CloneComment(CodeReviewComment comment)
    {
        return new CodeReviewComment
        {
            Id = comment.Id,
            FilePath = comment.FilePath,
            StartLine = comment.StartLine,
            EndLine = comment.EndLine,
            CommentText = comment.CommentText,
            CommentType = comment.CommentType,
            Severity = comment.Severity,
            SuggestedFix = comment.SuggestedFix,
            Posted = comment.Posted
        };
    }

    private static int? RemapToNearestValidRightSideLine(
        CodeReviewComment comment,
        Dictionary<string, List<int>> validRightSideLinesByFile)
    {
        var normalizedPath = NormalizePath(comment.FilePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        if (!validRightSideLinesByFile.TryGetValue(normalizedPath, out var validLines) || validLines.Count == 0)
        {
            return null;
        }

        var requestedLine = comment.StartLine > 0 ? comment.StartLine : 1;
        if (validLines.BinarySearch(requestedLine) >= 0)
        {
            return null;
        }

        var mappedLine = validLines
            .OrderBy(line => Math.Abs(line - requestedLine))
            .ThenBy(line => line)
            .First();

        comment.StartLine = mappedLine;
        comment.EndLine = Math.Max(mappedLine, comment.EndLine > 0 ? comment.EndLine : mappedLine);
        return mappedLine;
    }

    private static Dictionary<string, List<int>> BuildValidRightSideLineLookup(List<PullRequestFile> files)
    {
        var lookup = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var normalizedPath = NormalizePath(file.Path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            var lines = ExtractRightSideLinesFromUnifiedDiff(file.UnifiedDiff);
            if (lines.Count == 0)
            {
                continue;
            }

            lookup[normalizedPath] = lines;
        }

        return lookup;
    }

    private static List<int> ExtractRightSideLinesFromUnifiedDiff(string? unifiedDiff)
    {
        var lines = new SortedSet<int>();
        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return lines.ToList();
        }

        var currentRightLine = 0;
        var inHunk = false;
        var diffLines = unifiedDiff.Split('\n');

        foreach (var rawLine in diffLines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = Regex.Match(line, "@@ -\\d+(?:,\\d+)? \\+(?<start>\\d+)(?:,\\d+)? @@");
                if (!match.Success)
                {
                    inHunk = false;
                    continue;
                }

                currentRightLine = int.Parse(match.Groups["start"].Value);
                inHunk = true;
                continue;
            }

            if (!inHunk)
            {
                continue;
            }

            if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                lines.Add(currentRightLine);
                currentRightLine++;
                continue;
            }

            if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(currentRightLine);
            currentRightLine++;
        }

        return lines.ToList();
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.StartsWith("/", StringComparison.Ordinal)
            ? path
            : "/" + path;
    }

    private string? GetOptionalAdoAccessTokenHeader()
    {
        if (!Request.Headers.TryGetValue("X-Ado-Access-Token", out var headerValues))
        {
            return null;
        }

        var token = headerValues.FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    /// <summary>
    /// Records a thumbs-up / thumbs-down for a posted review comment.
    /// Invoked via a clickable link embedded in the comment footer.
    /// GET /api/codereview/feedback?id={commentId}&rating=1|0
    /// </summary>
    [HttpGet("feedback")]
    public async Task<IActionResult> RecordFeedback(
        [FromQuery] string id,
        [FromQuery] int rating,
        [FromQuery] int prId = 0,
        [FromQuery] string project = "",
        [FromQuery] string repository = "",
        [FromQuery] string filePath = "",
        [FromQuery] string severity = "",
        [FromQuery] string commentType = "")
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest("id is required");
        if (rating != 0 && rating != 1)
            return BadRequest("rating must be 0 (not helpful) or 1 (helpful)");

        await _feedbackService.RecordAsync(id, prId, project, repository, filePath, severity, commentType, rating == 1);

        _logger.LogInformation("Feedback recorded: commentId={CommentId} helpful={Helpful}", id, rating == 1);

        return Content(
            rating == 1
                ? "Thanks for the feedback! 👍 We're glad the comment was helpful."
                : "Thanks for the feedback! 👎 We'll use this to improve future reviews.",
            "text/plain");
    }

    /// <summary>
    /// Fetches current ADO thread statuses for a PR and records fixed/wontFix/etc. per comment.
    /// Call this after reviewers have had time to act on comments.
    /// POST /api/codereview/feedback/sync
    /// Body: { "project": "...", "repository": "...", "pullRequestId": 123 }
    /// </summary>
    [HttpPost("feedback/sync")]
    public async Task<IActionResult> SyncResolutions([FromBody] FeedbackSyncRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Project) || string.IsNullOrWhiteSpace(request.Repository) || request.PullRequestId <= 0)
            return BadRequest("project, repository, and pullRequestId are required");

        var repoInfo = await _adoClient.GetRepositoryAsync(request.Project, request.Repository);
        if (repoInfo == null)
            return NotFound($"Repository '{request.Repository}' not found in project '{request.Project}'");

        var statuses = await _adoClient.GetAgentThreadStatusesAsync(request.Project, repoInfo.Id, request.PullRequestId);
        var synced = await _feedbackService.SyncResolutionsAsync(request.Project, request.Repository, request.PullRequestId, statuses);

        var summary = statuses.GroupBy(s => s.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        _logger.LogInformation("Synced {Count} thread resolutions for PR {PrId}: {Summary}",
            synced, request.PullRequestId, string.Join(", ", summary.Select(kv => $"{kv.Key}={kv.Value}")));

        return Ok(new
        {
            synced,
            pullRequestId = request.PullRequestId,
            summary
        });
    }

    /// <summary>
    /// Returns aggregated feedback metrics.
    /// GET /api/codereview/feedback/metrics
    /// </summary>
    [HttpGet("feedback/metrics")]
    public IActionResult GetFeedbackMetrics()
    {
        var metrics = _feedbackService.GetMetrics();
        return Ok(metrics);
    }
}

public class ReviewRequest
{
    public string Project { get; set; } = "MyProject";
    public string Repository { get; set; } = "";
    public int PullRequestId { get; set; }
}

public class ReviewByLinkRequest
{
    public string PullRequestLink { get; set; } = string.Empty;
}

public class PostCommentRequest
{
    public string CommentId { get; set; } = "";
}

public class ConfigRequest
{
    public string Organization { get; set; } = "";
    public string PersonalAccessToken { get; set; } = "";
    public string ChatEndpoint { get; set; } = "";
    public string ChatApiKey { get; set; } = "";
    public string ChatDeployment { get; set; } = "gpt-4";
    public string ChatApiVersion { get; set; } = "2024-02-01";
    public string EmbeddingEndpoint { get; set; } = "";
    public string EmbeddingApiKey { get; set; } = "";
    public string EmbeddingDeployment { get; set; } = "text-embedding-ada-002";
    public string EmbeddingApiVersion { get; set; } = "2024-02-01";
}

public class IndexRequest
{
    public string Project { get; set; } = "MyProject";
    public string Repository { get; set; } = "";
    public string Branch { get; set; } = "master";
}

public class FeedbackSyncRequest
{
    public string Project { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public int PullRequestId { get; set; }
}

public class PostConsolidatedCommentsRequest
{
    public string Project       { get; set; } = string.Empty;
    public string Repository    { get; set; } = string.Empty;
    public int    PullRequestId { get; set; }
    public List<IncomingReviewComment> Comments { get; set; } = new();
}

public class IncomingReviewComment
{
    public string FilePath     { get; set; } = string.Empty;
    public int    StartLine    { get; set; } = 1;
    public int    EndLine      { get; set; }
    public string CommentText  { get; set; } = string.Empty;
    public string? Severity    { get; set; }   // high | medium | low
    public string? CommentType { get; set; }   // issue | suggestion | nitpick
    public string? SuggestedFix { get; set; }
    public double  Confidence  { get; set; }
}

public class ReviewResult
{
    public PullRequest PullRequest { get; set; } = null!;
    public List<PullRequestFile> Files { get; set; } = new();
    public List<CodeReviewComment> Comments { get; set; } = new();
    public string Project { get; set; } = "";
    public string Repository { get; set; } = "";
}

public class ProjectInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<string> Repositories { get; set; } = new();
}

public class ParsedPullRequestLink
{
    public string Organization { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public int PullRequestId { get; set; }
}

public class ReviewByLinkExecutionResult
{
    public string PullRequestLink { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public int PullRequestId { get; set; }
    public List<PullRequestFile> Files { get; set; } = new();
    public List<CodeReviewComment> Comments { get; set; } = new();
}

public class ReviewByLinkAndPostJobStatus
{
    public string JobId { get; set; } = string.Empty;
    public string PullRequestLink { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public int PullRequestId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int TotalComments { get; set; }
    public int HighPriorityComments { get; set; }
    public int PostedHighPriorityComments { get; set; }
    public int SkippedHighPriorityComments { get; set; }
    public List<CodeReviewComment> Comments { get; set; } = new();
    public List<ReviewPostingFailure> PostingFailures { get; set; } = new();
}

public class ReviewPostingFailure
{
    public string CommentId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}
