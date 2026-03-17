using CodeReviewAgent.Agents;
using CodeReviewAgent.Models;
using CodeReviewAgent.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
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
    private readonly ILogger<CodeReviewController> _logger;
    private static ReviewResult? _currentReview;
    private static readonly HashSet<string> _indexingInProgress = new();
    private static readonly object _indexingLock = new();
    private static readonly ConcurrentDictionary<string, ReviewByLinkAndPostJobStatus> _reviewByLinkAndPostJobs = new();
    private static readonly TimeSpan ReviewByLinkAndPostSyncWait = TimeSpan.FromSeconds(220);

    public CodeReviewController(
        CodeReviewAgentService codeReviewAgent,
        AzureDevOpsMcpClient adoClient,
        CodeReviewService reviewService,
        AdoConfigurationService adoConfig,
        ChatConfigurationService chatConfig,
        EmbeddingConfigurationService embeddingConfig,
        CodebaseContextService codebaseContextService,
        ILogger<CodeReviewController> logger)
    {
        _codeReviewAgent = codeReviewAgent;
        _adoClient = adoClient;
        _reviewService = reviewService;
        _adoConfig = adoConfig;
        _chatConfig = chatConfig;
        _embeddingConfig = embeddingConfig;
        _codebaseContextService = codebaseContextService;
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

        var backgroundTask = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation(
                    "Starting review-by-link-and-post job {JobId} for PR {PullRequestId} in {Project}/{Repository}",
                    jobId,
                    parsed.PullRequestId,
                    parsed.Project,
                    parsed.Repository);

                var reviewOutput = await RunReviewByLinkCoreAsync(request.PullRequestLink, parsed, adoAccessTokenOverride);
                var highPriorityComments = reviewOutput.Comments
                    .Where(IsHighPriorityComment)
                    .ToList();
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
                            comment.LineNumber,
                            remappedLine.Value);
                    }

                    var fingerprint = AzureDevOpsRestClient.BuildCommentFingerprint(commentToPost);
                    var positionKey = AzureDevOpsRestClient.BuildCommentPositionKey(commentToPost);
                    if (existingFingerprints.Contains(fingerprint) || existingPositionKeys.Contains(positionKey))
                    {
                        skippedCount++;
                        comment.Posted = true;
                        comment.LineNumber = commentToPost.LineNumber;
                        _logger.LogInformation(
                            "Skipping duplicate high-priority comment for PR {PullRequestId} at {FilePath}:{LineNumber}",
                            reviewOutput.PullRequestId,
                            commentToPost.FilePath,
                            commentToPost.LineNumber);
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
                        comment.LineNumber = commentToPost.LineNumber;
                        postedCount++;
                        existingFingerprints.Add(fingerprint);
                        existingPositionKeys.Add(positionKey);
                    }
                    else
                    {
                        postingFailures.Add(new ReviewPostingFailure
                        {
                            CommentId = comment.Id,
                            FilePath = commentToPost.FilePath,
                            LineNumber = commentToPost.LineNumber,
                            Severity = comment.Severity,
                            Stage = postResult.Stage,
                            StatusCode = postResult.StatusCode,
                            ErrorMessage = postResult.ErrorMessage
                        });

                        _logger.LogWarning(
                            "Failed to post high-priority comment for PR {PullRequestId} at {FilePath}:{LineNumber}. Stage={Stage}, StatusCode={StatusCode}, Error={Error}",
                            reviewOutput.PullRequestId,
                            commentToPost.FilePath,
                            commentToPost.LineNumber,
                            postResult.Stage,
                            postResult.StatusCode,
                            postResult.ErrorMessage);
                    }
                }

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

    private static bool IsHighPriorityComment(CodeReviewComment comment)
    {
        var severity = comment.Severity?.Trim();
        return string.Equals(severity, "high", StringComparison.OrdinalIgnoreCase)
            || string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase);
    }

    private static CodeReviewComment CloneComment(CodeReviewComment comment)
    {
        return new CodeReviewComment
        {
            Id = comment.Id,
            FilePath = comment.FilePath,
            LineNumber = comment.LineNumber,
            CommentText = comment.CommentText,
            CommentType = comment.CommentType,
            Severity = comment.Severity,
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

        var requestedLine = comment.LineNumber > 0 ? comment.LineNumber : 1;
        if (validLines.BinarySearch(requestedLine) >= 0)
        {
            return null;
        }

        var mappedLine = validLines
            .OrderBy(line => Math.Abs(line - requestedLine))
            .ThenBy(line => line)
            .First();

        comment.LineNumber = mappedLine;
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
    public int LineNumber { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}
