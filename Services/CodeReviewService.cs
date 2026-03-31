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
    private readonly AzureDevOpsRestClient _adoRestClient;
    private readonly CodebaseContextService _codebaseContextService;

    public CodeReviewService(
        IChatClient chatClient,
        ILogger<CodeReviewService> logger,
        CodeReviewOrchestrator orchestrator,
        AzureDevOpsMcpClient adoClient,
        AzureDevOpsRestClient adoRestClient,
        CodebaseContextService codebaseContextService)
    {
        _chatClient = chatClient;
        _logger = logger;
        _orchestrator = orchestrator;
        _adoClient = adoClient;
        _adoRestClient = adoRestClient;
        _codebaseContextService = codebaseContextService;
    }

    public async Task<List<CodeReviewComment>> ReviewPullRequestAsync(
        PullRequest pullRequest,
        List<PullRequestFile> files,
        string project,
        string repository)
    {
        _logger.LogInformation("Starting code review for PR {PullRequestId}: {Title}", pullRequest.Id, pullRequest.Title);

        // Fetch existing reviewer threads and per-file git history in parallel
        var threadSummaryTask = _adoRestClient.GetExistingThreadSummaryAsync(project, repository, pullRequest.Id);

        var reviewableFiles = files.Where(f => !string.IsNullOrEmpty(Path.GetExtension(f.Path))).ToList();
        var historyTasks = reviewableFiles.Select(async f =>
        {
            var history = await _adoRestClient.GetFileCommitHistoryAsync(project, repository, f.Path, maxCommits: 5);
            return (f.Path, history);
        });
        var historyResults = await Task.WhenAll(historyTasks);

        var existingThreadSummary = await threadSummaryTask;
        if (!string.IsNullOrEmpty(existingThreadSummary))
            _logger.LogInformation("📝 Fetched existing PR threads for context ({Chars} chars)", existingThreadSummary.Length);

        // Build git blame context — per-file commit history to catch regressions
        var gitBlameContext = new StringBuilder();
        var filesWithHistory = historyResults.Where(r => !string.IsNullOrEmpty(r.history)).ToList();
        if (filesWithHistory.Count > 0)
        {
            gitBlameContext.AppendLine("## Git History per Changed File (last 5 commits — detect regressions)");
            foreach (var (path, history) in filesWithHistory)
            {
                gitBlameContext.AppendLine($"### {path}");
                gitBlameContext.Append(history);
            }
            _logger.LogInformation("📜 Git history context: {FileCount} files, {Chars} chars",
                filesWithHistory.Count, gitBlameContext.Length);
        }

        // Build basic codebase context from changed files only (no REST API calls)
        var basicContext = BuildCodebaseContextFromChangedFiles(files);

        _logger.LogInformation("Basic codebase context built for {ChangedFileCount} changed files",
            files.Count);

        // Auto-refresh the RAG index if the repo has new commits since last index
        if (_codebaseContextService.IsRepositoryIndexed(repository))
        {
            var branch = StripRefHeadsPrefix(pullRequest.TargetBranch);
            try
            {
                var refreshed = await _codebaseContextService.RefreshIndexAsync(project, repository, branch);
                _logger.LogInformation(refreshed switch
                {
                    0  => "✅ RAG index is up-to-date",
                    -1 => "🔄 RAG index rebuilt (full re-index)",
                    _  => $"🔄 RAG index refreshed ({refreshed} chunks updated)"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Auto-refresh failed — proceeding with existing index");
            }
        }

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

        // Prepend git history and existing reviewer comments so agents know full context
        var prefixBuilder = new StringBuilder();
        if (gitBlameContext.Length > 0)
            prefixBuilder.AppendLine(gitBlameContext.ToString());
        if (!string.IsNullOrEmpty(existingThreadSummary))
            prefixBuilder.AppendLine(existingThreadSummary);
        if (prefixBuilder.Length > 0)
            codebaseContext = prefixBuilder.ToString() + codebaseContext;

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

        const int MAX_CONTENT_CHARS = 40_000;
        var isLargeFile = (file.Content?.Length ?? 0) > MAX_CONTENT_CHARS;

        if (!isLargeFile && !string.IsNullOrEmpty(file.PreviousContent) && file.ChangeType != "add")
        {
            prompt.AppendLine("Previous version:");
            prompt.AppendLine("```");
            prompt.AppendLine(file.PreviousContent);
            prompt.AppendLine("```");
            prompt.AppendLine();
        }

        if (isLargeFile)
        {
            prompt.AppendLine($"[File is large ({file.Content!.Length:N0} chars). Showing diff only to avoid context limits.]");
            prompt.AppendLine("Diff:");
            prompt.AppendLine("```");
            prompt.AppendLine(file.UnifiedDiff);
            prompt.AppendLine("```");
        }
        else
        {
            prompt.AppendLine("Current version:");
            prompt.AppendLine("```");
            prompt.AppendLine(file.Content);
            prompt.AppendLine("```");
        }
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
                    {
                        comment.StartLine = lineNumber;
                        comment.EndLine = lineNumber;
                    }
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

    private static string StripRefHeadsPrefix(string? fullRef)
    {
        if (string.IsNullOrWhiteSpace(fullRef)) return "master";
        const string prefix = "refs/heads/";
        return fullRef.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fullRef[prefix.Length..]
            : fullRef;
    }
}