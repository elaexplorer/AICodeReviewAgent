using CodeReviewAgent.Models;
using CodeReviewAgent.Services;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Agents;

public class CodeReviewAgentService
{
    private readonly AzureDevOpsMcpClient _adoClient;
    private readonly CodeReviewService _reviewService;
    private readonly ILogger<CodeReviewAgentService> _logger;

    public CodeReviewAgentService(
        AzureDevOpsMcpClient adoClient,
        CodeReviewService reviewService,
        ILogger<CodeReviewAgentService> logger)
    {
        _adoClient = adoClient;
        _reviewService = reviewService;
        _logger = logger;
    }

    public async Task<bool> ReviewPullRequestAsync(string project, string repository, int pullRequestId)
    {
        try
        {
            _logger.LogInformation("Starting code review for PR {PullRequestId} in repository {Repository} (project {Project})",
                pullRequestId, repository, project);

            // Step 1: Fetch PR details
            var pullRequest = await _adoClient.GetPullRequestAsync(project, repository, pullRequestId);
            if (pullRequest == null)
            {
                _logger.LogError("Could not fetch pull request {PullRequestId}", pullRequestId);
                return false;
            }

            _logger.LogInformation("Fetched PR: {Title} by {Author}", pullRequest.Title, pullRequest.CreatedBy.DisplayName);

            // Step 2: Get PR files and changes
            var files = await _adoClient.GetPullRequestFilesAsync(project, repository, pullRequestId);
            _logger.LogInformation("Found {FileCount} files to review", files.Count);

            if (files.Count == 0)
            {
                _logger.LogInformation("No files to review in PR {PullRequestId}", pullRequestId);
                return true;
            }

            // Step 3: Perform code review with orchestration
            var reviewComments = await _reviewService.ReviewPullRequestAsync(pullRequest, files, project, repository);
            _logger.LogInformation("Generated {CommentCount} review comments", reviewComments.Count);

            // Step 4: Post comments to the PR
            var successCount = 0;
            foreach (var comment in reviewComments)
            {
                var posted = await _adoClient.PostCommentAsync(project, repository, pullRequestId, comment);
                if (posted)
                    successCount++;
            }

            _logger.LogInformation("Successfully posted {SuccessCount} out of {TotalCount} comments",
                successCount, reviewComments.Count);

            return successCount == reviewComments.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing code review for PR {PullRequestId}", pullRequestId);
            return false;
        }
    }

    public async Task<string> GetReviewSummaryAsync(string project, string repository, int pullRequestId)
    {
        try
        {
            var pullRequest = await _adoClient.GetPullRequestAsync(project, repository, pullRequestId);
            if (pullRequest == null)
                return "Could not fetch pull request details.";

            var files = await _adoClient.GetPullRequestFilesAsync(project, repository, pullRequestId);
            var reviewComments = await _reviewService.ReviewPullRequestAsync(pullRequest, files, project, repository);

            var summary = $"""
                Code Review Summary for PR #{pullRequest.Id}: {pullRequest.Title}

                Author: {pullRequest.CreatedBy.DisplayName}
                Created: {pullRequest.CreationDate:yyyy-MM-dd HH:mm}
                Source: {pullRequest.SourceBranch} → Target: {pullRequest.TargetBranch}

                Files Reviewed: {files.Count}
                Total Comments: {reviewComments.Count}

                Issues by Severity:
                - High: {reviewComments.Count(c => c.Severity == "high")}
                - Medium: {reviewComments.Count(c => c.Severity == "medium")}
                - Low: {reviewComments.Count(c => c.Severity == "low")}

                Comment Types:
                - Issues: {reviewComments.Count(c => c.CommentType == "issue")}
                - Suggestions: {reviewComments.Count(c => c.CommentType == "suggestion")}
                - Nitpicks: {reviewComments.Count(c => c.CommentType == "nitpick")}
                """;

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating review summary for PR {PullRequestId}", pullRequestId);
            return "Error generating review summary.";
        }
    }
}