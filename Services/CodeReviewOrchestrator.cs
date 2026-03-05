using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using CodeReviewAgent.Agents;
using CodeReviewAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;

namespace CodeReviewAgent.Services;

/// <summary>
/// Orchestrates code review by routing files to appropriate language-specific agents
/// using Microsoft.Agents.AI multi-agent coordination
/// </summary>
public class CodeReviewOrchestrator
{
    private readonly ILogger<CodeReviewOrchestrator> _logger;
    private readonly IChatClient _chatClient;
    private readonly Dictionary<string, ILanguageReviewAgent> _agents;
    private readonly Dictionary<string, string> _extensionToLanguage;

    public CodeReviewOrchestrator(
        ILogger<CodeReviewOrchestrator> logger,
        IChatClient chatClient,
        IEnumerable<ILanguageReviewAgent> agents)
    {
        _logger = logger;
        _chatClient = chatClient; // Used for general reviews when no specialized agent exists
        _agents = agents.ToDictionary(a => a.Language, a => a);
        _extensionToLanguage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Build extension to language mapping
        foreach (var agent in agents)
        {
            foreach (var ext in agent.FileExtensions)
            {
                _extensionToLanguage[ext] = agent.Language;
            }
        }

        // No plugin registration needed - manual routing
        _logger.LogInformation("Registered {AgentCount} language review agents: {Languages}",
            _agents.Count, string.Join(", ", _agents.Keys));
    }

    /// <summary>
    /// Review files by orchestrating language-specific agents
    /// </summary>
    public async Task<List<CodeReviewComment>> ReviewFilesAsync(
        List<PullRequestFile> files,
        string codebaseContext)
    {
        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║ FILE PROCESSING STRATEGY                                   ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
        _logger.LogInformation("📂 Processing {FileCount} files in PARALLEL for faster reviews", files.Count);
        _logger.LogInformation("   Each file gets its own RAG context and LLM call");
        _logger.LogInformation("   Files: {Files}", string.Join(", ", files.Select(f => Path.GetFileName(f.Path))));
        
        var overallStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var failures = new ConcurrentBag<string>();
        
        // Process files in parallel for faster reviews
        var reviewTasks = files.Select(async file =>
        {
            try
            {
                var extension = Path.GetExtension(file.Path);

                if (string.IsNullOrEmpty(extension))
                {
                    _logger.LogWarning("Skipping file without extension: {FilePath}", file.Path);
                    return new List<CodeReviewComment>();
                }

                // Determine which agent should review this file
                if (_extensionToLanguage.TryGetValue(extension, out var language) &&
                    _agents.TryGetValue(language, out var agent))
                {
                    _logger.LogInformation("Routing {FilePath} to {Language} agent", file.Path, language);
                    return await agent.ReviewFileAsync(file, codebaseContext);
                }
                else
                {
                    _logger.LogInformation("No specialized agent for {Extension}, using general review for {FilePath}",
                        extension, file.Path);
                    return await ReviewWithGeneralAgentAsync(file, codebaseContext);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reviewing file {FilePath}", file.Path);
                failures.Add($"{file.Path}: {ex.Message}");
                return new List<CodeReviewComment>();
            }
        });

        var results = await Task.WhenAll(reviewTasks);
        overallStopwatch.Stop();
        
        var allComments = results.SelectMany(comments => comments).ToList();
        
        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║ PARALLEL PROCESSING COMPLETE                              ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
        _logger.LogInformation("⏱️  Total parallel processing time: {TotalMs}ms ({TotalSec:F2} seconds)", 
            overallStopwatch.ElapsedMilliseconds, overallStopwatch.Elapsed.TotalSeconds);
        _logger.LogInformation("📊 Processing results:");
        _logger.LogInformation("   Files processed: {FileCount}", files.Count);
        _logger.LogInformation("   Total comments generated: {CommentCount}", allComments.Count);
        _logger.LogInformation("   Average time per file: {AvgMs}ms", 
            files.Count > 0 ? overallStopwatch.ElapsedMilliseconds / files.Count : 0);

        if (!failures.IsEmpty)
        {
            _logger.LogWarning("   Files with review errors: {FailureCount}", failures.Count);
        }

        _logger.LogInformation("════════════════════════════════════════════════════════════");

        if (!failures.IsEmpty && allComments.Count == 0)
        {
            var errorDetails = string.Join(" | ", failures.Take(3));
            throw new InvalidOperationException(
                $"Code review failed for all files. {errorDetails}. Check LLM chat credentials/configuration and retry.");
        }
        
        return allComments;
    }

    /// <summary>
    /// General review for files without specialized agents
    /// </summary>
    private async Task<List<CodeReviewComment>> ReviewWithGeneralAgentAsync(
        PullRequestFile file,
        string codebaseContext)
    {
        try
        {
            // Cap content to avoid context length errors. For large/generated files, prefer the diff.
            const int MAX_CONTENT_CHARS = 40_000;
            var isLargeFile = (file.Content?.Length ?? 0) > MAX_CONTENT_CHARS;
            var contentSection = isLargeFile
                ? $"[File is large ({file.Content!.Length:N0} chars). Showing diff only — full content truncated to avoid context limits.]\n\nDiff:\n```\n{file.UnifiedDiff}\n```"
                : $"```\n{file.Content}\n```";

            // Omit previous content for large files (the diff captures what changed)
            var previousSection = (!isLargeFile && !string.IsNullOrEmpty(file.PreviousContent))
                ? $"Previous Content:\n```\n{file.PreviousContent}\n```\n"
                : string.Empty;

            var prompt = $$$"""
                You are a general code reviewer with broad knowledge of software engineering best practices.

                Review the following file that was changed in a pull request:

                File Path: {{{file.Path}}}
                Change Type: {{{file.ChangeType}}}

                Current Content:
                {{{contentSection}}}

                {{{previousSection}}}
                Codebase Context:
                {{{codebaseContext}}}

                Provide a code review focusing on:
                1. Security vulnerabilities
                2. Potential bugs and logic errors
                3. Code quality and maintainability
                4. Best practices

                For each issue found, provide:
                - Line number (if applicable)
                - Severity (high/medium/low)
                - Type (issue/suggestion/nitpick)
                - Clear explanation and recommendation

                Return your response as a JSON array of objects with this structure:
                [
                  {
                    "lineNumber": 10,
                    "severity": "high",
                    "type": "issue",
                    "comment": "Detailed explanation and recommendation"
                  }
                ]

                If no issues are found, return an empty array: []
                """;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You are a general code reviewer with broad knowledge of software engineering best practices."),
                new(ChatRole.User, prompt)
            };

            // Log LLM request details
            _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║ LLM REQUEST: General Code Review                           ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("📤 SENDING TO LLM:");
            _logger.LogInformation("   File: {FilePath}", file.Path);
            _logger.LogInformation("   System prompt length: {Length} chars", messages[0].Text?.Length ?? 0);
            _logger.LogInformation("   User prompt length: {Length} chars", prompt.Length);
            _logger.LogInformation("   Total prompt length: {Length} chars", (messages[0].Text?.Length ?? 0) + prompt.Length);
            _logger.LogInformation("   Codebase context included: {HasContext}", !string.IsNullOrEmpty(codebaseContext));
            _logger.LogInformation("📝 FULL USER PROMPT:\n{Prompt}", prompt);

            var stopwatch = Stopwatch.StartNew();
            ChatResponse response = await _chatClient.GetResponseAsync(messages);
            stopwatch.Stop();

            var responseText = response.Text ?? "[]";

            // Log LLM response details
            _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║ LLM RESPONSE: General Code Review                          ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("📥 RECEIVED FROM LLM:");
            _logger.LogInformation("   Response length: {Length} chars", responseText.Length);
            _logger.LogInformation("   ⏱️  Time taken: {ElapsedMs} ms ({ElapsedSec:F2} seconds)",
                stopwatch.ElapsedMilliseconds, stopwatch.Elapsed.TotalSeconds);

            // Log token usage if available
            if (response.Usage != null)
            {
                _logger.LogInformation("📊 TOKEN USAGE:");
                _logger.LogInformation("   Input tokens: {InputTokens}", response.Usage.InputTokenCount ?? 0);
                _logger.LogInformation("   Output tokens: {OutputTokens}", response.Usage.OutputTokenCount ?? 0);
                _logger.LogInformation("   Total tokens: {TotalTokens}",
                    (response.Usage.InputTokenCount ?? 0) + (response.Usage.OutputTokenCount ?? 0));

                // Estimate cost (approximate pricing for GPT-4)
                var inputCost = (response.Usage.InputTokenCount ?? 0) * 0.00003m; // $0.03 per 1K input
                var outputCost = (response.Usage.OutputTokenCount ?? 0) * 0.00006m; // $0.06 per 1K output
                _logger.LogInformation("   💰 Estimated cost: ${TotalCost:F4} (input: ${InputCost:F4}, output: ${OutputCost:F4})",
                    inputCost + outputCost, inputCost, outputCost);
            }
            else
            {
                _logger.LogInformation("📊 TOKEN USAGE: Not available from response");
            }

            _logger.LogInformation("📝 FULL LLM RESPONSE:\n{Response}", responseText);
            _logger.LogInformation("════════════════════════════════════════════════════════════");

            return ParseReviewComments(responseText, file.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in general review for {FilePath}", file.Path);
            return new List<CodeReviewComment>();
        }
    }

    private List<CodeReviewComment> ParseReviewComments(string jsonResponse, string filePath)
    {
        try
        {
            jsonResponse = ExtractJsonPayload(jsonResponse);

            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var parsedComments = System.Text.Json.JsonSerializer.Deserialize<List<ReviewCommentJson>>(jsonResponse, options)
                ?? new List<ReviewCommentJson>();

            return parsedComments.Select(c => new CodeReviewComment
            {
                FilePath = filePath,
                LineNumber = c.LineNumber,
                Severity = c.Severity?.ToLower() ?? "low",
                CommentType = c.Type?.ToLower() ?? "suggestion",
                CommentText = c.Comment ?? string.Empty
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing review comments JSON. Response preview: {Preview}",
                jsonResponse.Length > 500 ? jsonResponse.Substring(0, 500) : jsonResponse);
            return new List<CodeReviewComment>();
        }
    }

    private static string ExtractJsonPayload(string response)
    {
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```json"))
        {
            trimmed = trimmed.Substring(7);
        }

        if (trimmed.StartsWith("```"))
        {
            trimmed = trimmed.Substring(3);
        }

        if (trimmed.EndsWith("```"))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 3);
        }

        trimmed = trimmed.Trim();

        // Most responses should be an array. If the model adds leading or trailing text,
        // extract only the first complete JSON array segment.
        var firstArray = trimmed.IndexOf('[');
        var lastArray = trimmed.LastIndexOf(']');
        if (firstArray >= 0 && lastArray > firstArray)
        {
            return trimmed.Substring(firstArray, lastArray - firstArray + 1);
        }

        // Some models wrap the output as { "comments": [ ... ] }.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("comments", out var comments) &&
                comments.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return comments.GetRawText();
            }
        }
        catch
        {
            // Let the caller attempt normal deserialize and surface the parse failure in logs.
        }

        return trimmed;
    }

    private class ReviewCommentJson
    {
        public int LineNumber { get; set; }
        public string? Severity { get; set; }
        public string? Type { get; set; }
        public string? Comment { get; set; }
    }
}
