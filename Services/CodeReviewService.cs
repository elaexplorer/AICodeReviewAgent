using CodeReviewAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Text;

namespace CodeReviewAgent.Services;

public class CodeReviewService
{
    private readonly IChatCompletionService _chatCompletion;
    private readonly ILogger<CodeReviewService> _logger;
    private readonly CodeReviewOrchestrator _orchestrator;
    private readonly AzureDevOpsMcpClient _adoClient;

    public CodeReviewService(
        IChatCompletionService chatCompletion,
        ILogger<CodeReviewService> logger,
        CodeReviewOrchestrator orchestrator,
        AzureDevOpsMcpClient adoClient)
    {
        _chatCompletion = chatCompletion;
        _logger = logger;
        _orchestrator = orchestrator;
        _adoClient = adoClient;
    }

    public async Task<List<CodeReviewComment>> ReviewPullRequestAsync(
        PullRequest pullRequest,
        List<PullRequestFile> files,
        string project,
        string repository)
    {
        _logger.LogInformation("Starting code review for PR {PullRequestId}: {Title}", pullRequest.Id, pullRequest.Title);

        // Fetch and cache repository structure for context
        var branch = pullRequest.TargetBranch.Replace("refs/heads/", "");
        var repoStructure = await _adoClient.GetRepositoryStructureAsync(project, repository, branch);

        // Build codebase context summary
        var codebaseContext = BuildCodebaseContext(repoStructure, files);

        _logger.LogInformation("Codebase context built with {FileCount} total files, reviewing {ChangedFileCount} changed files",
            repoStructure.Count, files.Count);

        // Use orchestrator to route reviews to language-specific agents
        var comments = await _orchestrator.ReviewFilesAsync(files, codebaseContext);

        _logger.LogInformation("Code review completed with {CommentCount} total comments", comments.Count);

        return comments;
    }

    private string BuildCodebaseContext(List<string> repoStructure, List<PullRequestFile> changedFiles)
    {
        var context = new StringBuilder();

        context.AppendLine("=== Codebase Structure ===");
        context.AppendLine($"Total files in repository: {repoStructure.Count}");
        context.AppendLine();

        // Group files by directory
        var directories = repoStructure
            .Select(f => Path.GetDirectoryName(f) ?? "/")
            .Distinct()
            .OrderBy(d => d)
            .Take(50); // Limit to avoid huge context

        context.AppendLine("Key directories:");
        foreach (var dir in directories)
        {
            var fileCount = repoStructure.Count(f => (Path.GetDirectoryName(f) ?? "/") == dir);
            context.AppendLine($"  {dir}: {fileCount} files");
        }
        context.AppendLine();

        // Language distribution
        var languageStats = repoStructure
            .Select(f => Path.GetExtension(f))
            .Where(ext => !string.IsNullOrEmpty(ext))
            .GroupBy(ext => ext)
            .OrderByDescending(g => g.Count())
            .Take(10);

        context.AppendLine("Language distribution:");
        foreach (var lang in languageStats)
        {
            context.AppendLine($"  {lang.Key}: {lang.Count()} files");
        }
        context.AppendLine();

        context.AppendLine("=== Files Changed in This PR ===");
        foreach (var file in changedFiles)
        {
            context.AppendLine($"  - {file.Path} ({file.ChangeType})");
        }

        return context.ToString();
    }

    private async Task<List<CodeReviewComment>> ReviewFileAsync(PullRequestFile file)
    {
        try
        {
            var prompt = BuildCodeReviewPrompt(file);

            var chatHistory = new ChatHistory();
            chatHistory.AddSystemMessage("You are an expert code reviewer. Analyze the provided code changes and provide constructive feedback.");
            chatHistory.AddUserMessage(prompt);

            var response = await _chatCompletion.GetChatMessageContentAsync(chatHistory);

            return ParseReviewResponse(response.Content ?? string.Empty, file.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reviewing file {FilePath}", file.Path);
            return new List<CodeReviewComment>();
        }
    }

    private string BuildCodeReviewPrompt(PullRequestFile file)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine($"Please review the following code changes for file: {file.Path}");
        prompt.AppendLine($"Change type: {file.ChangeType}");
        prompt.AppendLine();

        prompt.AppendLine("Focus on:");
        prompt.AppendLine("1. Code quality and best practices");
        prompt.AppendLine("2. Potential bugs or issues");
        prompt.AppendLine("3. Security vulnerabilities");
        prompt.AppendLine("4. Performance considerations");
        prompt.AppendLine("5. Maintainability and readability");
        prompt.AppendLine();

        if (!string.IsNullOrEmpty(file.PreviousContent) && file.ChangeType != "add")
        {
            prompt.AppendLine("Previous version:");
            prompt.AppendLine("```");
            prompt.AppendLine(file.PreviousContent);
            prompt.AppendLine("```");
            prompt.AppendLine();
        }

        prompt.AppendLine("Current version:");
        prompt.AppendLine("```");
        prompt.AppendLine(file.Content);
        prompt.AppendLine("```");
        prompt.AppendLine();

        prompt.AppendLine("Please provide your review in the following format:");
        prompt.AppendLine("For each issue found, use this structure:");
        prompt.AppendLine("COMMENT_START");
        prompt.AppendLine("Line: [line number]");
        prompt.AppendLine("Type: [suggestion|issue|nitpick]");
        prompt.AppendLine("Severity: [low|medium|high]");
        prompt.AppendLine("Message: [your comment]");
        prompt.AppendLine("COMMENT_END");
        prompt.AppendLine();
        prompt.AppendLine("If no issues are found, respond with: NO_ISSUES_FOUND");

        return prompt.ToString();
    }

    private List<CodeReviewComment> ParseReviewResponse(string response, string filePath)
    {
        var comments = new List<CodeReviewComment>();

        if (response.Contains("NO_ISSUES_FOUND"))
        {
            return comments;
        }

        var commentBlocks = response.Split("COMMENT_START", StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in commentBlocks)
        {
            if (!block.Contains("COMMENT_END"))
                continue;

            var content = block.Substring(0, block.IndexOf("COMMENT_END"));
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var comment = new CodeReviewComment
            {
                FilePath = filePath
            };

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("Line:"))
                {
                    if (int.TryParse(trimmedLine.Substring(5).Trim(), out var lineNumber))
                        comment.LineNumber = lineNumber;
                }
                else if (trimmedLine.StartsWith("Type:"))
                {
                    comment.CommentType = trimmedLine.Substring(5).Trim();
                }
                else if (trimmedLine.StartsWith("Severity:"))
                {
                    comment.Severity = trimmedLine.Substring(9).Trim();
                }
                else if (trimmedLine.StartsWith("Message:"))
                {
                    comment.CommentText = trimmedLine.Substring(8).Trim();
                }
            }

            if (!string.IsNullOrEmpty(comment.CommentText))
            {
                comments.Add(comment);
            }
        }

        return comments;
    }
}