using CodeReviewAgent.Agents;
using CodeReviewAgent.Models;
using CodeReviewAgent.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace CodeReviewAgent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CodeReviewController : ControllerBase
{
    private readonly CodeReviewAgentService _codeReviewAgent;
    private readonly AzureDevOpsMcpClient _adoClient;
    private readonly AdoConfigurationService _adoConfig;
    private readonly ChatConfigurationService _chatConfig;
    private readonly EmbeddingConfigurationService _embeddingConfig;
    private readonly CodebaseContextService _codebaseContextService;
    private readonly ILogger<CodeReviewController> _logger;
    private static ReviewResult? _currentReview;
    private static readonly HashSet<string> _indexingInProgress = new();
    private static readonly object _indexingLock = new();

    public CodeReviewController(
        CodeReviewAgentService codeReviewAgent,
        AzureDevOpsMcpClient adoClient,
        AdoConfigurationService adoConfig,
        ChatConfigurationService chatConfig,
        EmbeddingConfigurationService embeddingConfig,
        CodebaseContextService codebaseContextService,
        ILogger<CodeReviewController> logger)
    {
        _codeReviewAgent = codeReviewAgent;
        _adoClient = adoClient;
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
                return NotFound(new { error = "Pull request not found" });
            }

            // Get PR files
            var files = await _adoClient.GetPullRequestFilesAsync(
                request.Project, request.Repository, request.PullRequestId);

            // Perform code review
            var reviewService = HttpContext.RequestServices.GetRequiredService<CodeReviewService>();
            var comments = await reviewService.ReviewPullRequestAsync(
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
            _logger.LogInformation("Starting review from link for PR {PullRequestId} in {Project}/{Repository}",
                parsed.PullRequestId, parsed.Project, parsed.Repository);

            var pullRequest = await _adoClient.GetPullRequestAsync(
                parsed.Project, parsed.Repository, parsed.PullRequestId);

            if (pullRequest == null)
            {
                return NotFound(new { error = "Pull request not found" });
            }

            var files = await _adoClient.GetPullRequestFilesAsync(
                parsed.Project, parsed.Repository, parsed.PullRequestId);

            var reviewService = HttpContext.RequestServices.GetRequiredService<CodeReviewService>();
            var comments = await reviewService.ReviewPullRequestAsync(
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
                Name = "waimeabay",
                DisplayName = "Waimea Bay",
                Repositories = new List<string> { "waimeabay" }
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
                "waimeabay" => new List<string> { "waimeabay" },
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
    private void TriggerBackgroundIndexing(string project, string repository, string branch = "master")
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

                var chunksIndexed = await _codebaseContextService.IndexRepositoryAsync(project, repository, branch);

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
            var (isValid, errorMessage) = await _adoConfig.ValidateAndConfigureAsync(request.Organization, request.PersonalAccessToken);

            if (!isValid)
            {
                return BadRequest(new {
                    success = false,
                    error = errorMessage
                });
            }

            var (chatIsValid, chatError) = await _chatConfig.ValidateAndConfigureAsync(
                request.ChatEndpoint,
                request.ChatApiKey,
                request.ChatDeployment,
                request.ChatApiVersion);

            if (!chatIsValid)
            {
                return BadRequest(new {
                    success = false,
                    error = chatError
                });
            }

            var (embeddingIsValid, embeddingError) = await _embeddingConfig.ValidateAndConfigureAsync(
                request.EmbeddingEndpoint,
                request.EmbeddingApiKey,
                request.EmbeddingDeployment,
                request.EmbeddingApiVersion);

            if (!embeddingIsValid)
            {
                return BadRequest(new {
                    success = false,
                    error = embeddingError
                });
            }

            // Reinitialize ADO client with new credentials
            _adoClient.UpdateConfiguration(request.Organization, request.PersonalAccessToken);

            return Ok(new {
                success = true,
                message = "Configuration validated successfully",
                organization = request.Organization
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
                request.Branch);

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
