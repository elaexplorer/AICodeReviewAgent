using CodeReviewAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using System.Text;

namespace CodeReviewAgent.Services;

public class CodeReviewService
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<CodeReviewService> _logger;
    private readonly CodeReviewOrchestrator _orchestrator;
    private readonly AzureDevOpsMcpClient _adoClient;
    private readonly CodebaseContextService _codebaseContextService;

    public CodeReviewService(
        IChatClient chatClient,
        ILogger<CodeReviewService> logger,
        CodeReviewOrchestrator orchestrator,
        AzureDevOpsMcpClient adoClient,
        CodebaseContextService codebaseContextService)
    {
        _chatClient = chatClient;
        _logger = logger;
        _orchestrator = orchestrator;
        _adoClient = adoClient;
        _codebaseContextService = codebaseContextService;
    }

    public async Task<List<CodeReviewComment>> ReviewPullRequestAsync(
        PullRequest pullRequest,
        List<PullRequestFile> files,
        string project,
        string repository)
    {
        _logger.LogInformation("Starting code review for PR {PullRequestId}: {Title}", pullRequest.Id, pullRequest.Title);

        // Build basic codebase context from changed files only (no REST API calls)
        var basicContext = BuildCodebaseContextFromChangedFiles(files);

        _logger.LogInformation("Basic codebase context built for {ChangedFileCount} changed files",
            files.Count);

        // Check if RAG indexing is available
        string codebaseContext;
        if (_codebaseContextService.IsRepositoryIndexed(repository))
        {
            _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║ RAG CONTEXT: Repository is indexed, using semantic context ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("Repository '{Repository}' has {ChunkCount} indexed chunks",
                repository, _codebaseContextService.GetChunkCount(repository));

            // Build RAG-enhanced context for each file
            var ragContextBuilder = new StringBuilder();
            ragContextBuilder.AppendLine(basicContext);
            ragContextBuilder.AppendLine();
            ragContextBuilder.AppendLine("=== RAG-Enhanced Semantic Context ===");

            foreach (var file in files)
            {
                _logger.LogInformation("Building RAG context for file: {FilePath}", file.Path);
                var fileRagContext = await _codebaseContextService.BuildReviewContextAsync(
                    file, pullRequest, project, repository);

                if (!string.IsNullOrEmpty(fileRagContext))
                {
                    ragContextBuilder.AppendLine($"\n--- Context for {file.Path} ---");
                    ragContextBuilder.AppendLine(fileRagContext);
                }
            }

            codebaseContext = ragContextBuilder.ToString();
            _logger.LogInformation("RAG context built: {Length} total characters", codebaseContext.Length);
        }
        else
        {
            _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║ RAG CONTEXT: Repository NOT indexed                        ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("To enable RAG context, call POST /api/codereview/index first");
            _logger.LogInformation("Using basic directory-based context instead");
            codebaseContext = basicContext;
        }

        // Use orchestrator to route reviews to language-specific agents
        var comments = await _orchestrator.ReviewFilesAsync(files, codebaseContext);

        _logger.LogInformation("Code review completed with {CommentCount} total comments", comments.Count);

        return comments;
    }

    private string BuildCodebaseContextFromChangedFiles(List<PullRequestFile> changedFiles)
    {
        var context = new StringBuilder();

        context.AppendLine("=== Files Changed in This PR ===");
        foreach (var file in changedFiles)
        {
            context.AppendLine($"  - {file.Path} ({file.ChangeType})");
        }
        context.AppendLine();

        // Language distribution from changed files only
        var languageStats = changedFiles
            .Select(f => Path.GetExtension(f.Path))
            .Where(ext => !string.IsNullOrEmpty(ext))
            .GroupBy(ext => ext)
            .OrderByDescending(g => g.Count());

        context.AppendLine("Languages in this PR:");
        foreach (var lang in languageStats)
        {
            context.AppendLine($"  {lang.Key}: {lang.Count()} files");
        }
        context.AppendLine();

        // Directory distribution from changed files only
        var directories = changedFiles
            .Select(f => Path.GetDirectoryName(f.Path) ?? "/")
            .Distinct()
            .OrderBy(d => d);

        context.AppendLine("Directories affected:");
        foreach (var dir in directories)
        {
            var fileCount = changedFiles.Count(f => (Path.GetDirectoryName(f.Path) ?? "/") == dir);
            context.AppendLine($"  {dir}: {fileCount} files");
        }

        return context.ToString();
    }

    private async Task<List<CodeReviewComment>> ReviewFileAsync(PullRequestFile file)
    {
        try
        {
            var prompt = BuildCodeReviewPrompt(file);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You are an expert code reviewer. Analyze the provided code changes and provide constructive feedback."),
                new(ChatRole.User, prompt)
            };

            ChatResponse response = await _chatClient.GetResponseAsync(messages);

            return ParseReviewResponse(response.Text ?? string.Empty, file.Path);
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