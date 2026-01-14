using CodeReviewAgent.Agents;
using CodeReviewAgent.Models;
using CodeReviewAgent.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CodeReviewController : ControllerBase
{
    private readonly CodeReviewAgentService _codeReviewAgent;
    private readonly AzureDevOpsMcpClient _adoClient;
    private readonly AdoConfigurationService _adoConfig;
    private readonly ILogger<CodeReviewController> _logger;
    private static ReviewResult? _currentReview;

    public CodeReviewController(
        CodeReviewAgentService codeReviewAgent,
        AzureDevOpsMcpClient adoClient,
        AdoConfigurationService adoConfig,
        ILogger<CodeReviewController> logger)
    {
        _codeReviewAgent = codeReviewAgent;
        _adoClient = adoClient;
        _adoConfig = adoConfig;
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
        // Return predefined list of projects
        var projects = new List<ProjectInfo>
        {
            new ProjectInfo { Name = "SCC", DisplayName = "SCC", Repositories = new List<string> { "service-shared_framework_waimea" } },
            new ProjectInfo { Name = "MyProject", DisplayName = "My Project", Repositories = new List<string>() }
        };

        return Ok(projects);
    }

    [HttpGet("repositories/{project}")]
    public async Task<IActionResult> GetRepositories(string project)
    {
        try
        {
            // In a real implementation, you'd fetch repositories from ADO
            // For now, return hardcoded list based on project
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

            return Ok(prs);
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
        return Ok(new {
            isConfigured = _adoConfig.IsConfigured,
            organization = _adoConfig.Organization
        });
    }

    [HttpPost("config/validate")]
    public async Task<IActionResult> ValidateConfig([FromBody] ConfigRequest request)
    {
        try
        {
            var (isValid, errorMessage) = await _adoConfig.ValidateAndConfigureAsync(request.Organization, request.PersonalAccessToken);

            if (isValid)
            {
                // Reinitialize ADO client with new credentials
                _adoClient.UpdateConfiguration(request.Organization, request.PersonalAccessToken);

                return Ok(new {
                    success = true,
                    message = "Configuration validated successfully",
                    organization = request.Organization
                });
            }
            else
            {
                return BadRequest(new {
                    success = false,
                    error = errorMessage
                });
            }
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
        return Ok(new { success = true, message = "Configuration cleared" });
    }
}

public class ReviewRequest
{
    public string Project { get; set; } = "SCC";
    public string Repository { get; set; } = "";
    public int PullRequestId { get; set; }
}

public class PostCommentRequest
{
    public string CommentId { get; set; } = "";
}

public class ConfigRequest
{
    public string Organization { get; set; } = "";
    public string PersonalAccessToken { get; set; } = "";
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
