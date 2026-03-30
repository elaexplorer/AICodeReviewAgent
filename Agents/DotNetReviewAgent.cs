using System.ComponentModel;
using System.Diagnostics;
using CodeReviewAgent.Models;
using CodeReviewAgent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

namespace CodeReviewAgent.Agents;

/// <summary>
/// .NET/C# code review expert agent using Microsoft.Agents.AI
/// </summary>
public class DotNetReviewAgent : ILanguageReviewAgent
{
    private readonly ILogger<DotNetReviewAgent> _logger;
    private readonly AIAgent _agent;

    public string Language => "DotNet";
    public string[] FileExtensions => new[] { ".cs", ".csproj", ".cshtml", ".razor" };

    public DotNetReviewAgent(
        ILogger<DotNetReviewAgent> logger,
        IChatClient chatClient)
    {
        _logger = logger;

        // Create ChatClientAgent with specialized instructions
        _agent = new ChatClientAgent(
            chatClient,
            instructions: """
                You are an expert C#/.NET code reviewer with deep knowledge of:
                - C# best practices and coding standards
                - .NET Framework/.NET Core/.NET 5+ features and patterns
                - SOLID principles and design patterns
                - Common .NET security vulnerabilities (OWASP)
                - Performance optimization and memory management
                - Async/await patterns and threading
                - LINQ, Entity Framework, ASP.NET Core
                - Dependency injection and IoC patterns
                - Unit testing with xUnit/NUnit/MSTest

                CRITICAL RULES:
                1. ONLY comment on lines marked with '+' in the diff (new/modified lines)
                2. DO NOT comment on lines marked with '-' (removed lines) or context lines
                3. Each '+' line has an [Lxx] tag showing its actual line number in the file — e.g. +[L42] public void Foo()
                4. 'startLine' MUST be the [Lxx] number of the first '+' line of the issue; 'endLine' MUST be the [Lxx] number of the last '+' line of the issue (same as startLine for single-line issues)
                5. Provide your response as a JSON array of review comments
                6. DO NOT flag style, naming conventions, whitespace, formatting, missing XML docs, or anything a linter/formatter (e.g. Roslyn analyzers, StyleCop, EditorConfig) would catch automatically. Only report issues that carry real risk: bugs, security vulnerabilities, performance problems, compliance violations, or missing critical test coverage. Use "nitpick" sparingly and only when it prevents genuine confusion — not for cosmetic preferences.

                Severity values — choose exactly one:
                - "critical" : security vulnerability, data loss, crash, or auth bypass — MUST fix before merge
                - "high"     : bug causing incorrect or undefined behaviour
                - "medium"   : performance issue, resource leak, or non-critical bug
                - "low"      : minor improvement opportunity

                Type values — choose exactly one:
                - "issue"      : a bug or incorrect behaviour in the changed code
                - "suggestion" : a correctness or performance improvement
                - "compliance" : PII/sensitive data logging, missing audit trail, data-retention violation
                - "testing"    : missing or inadequate test coverage for a critical code path
                - "nitpick"    : minor style improvement (use sparingly)

                For each issue found, provide:
                - startLine    : [Lxx] number of the first '+' line of the issue
                - endLine      : [Lxx] number of the last '+' line of the issue (same as startLine for single-line issues)
                - severity     : one of critical/high/medium/low
                - type         : one of issue/suggestion/compliance/testing/nitpick
                - comment      : clear explanation of the problem
                - suggestedFix : a concrete fix — a code snippet showing the corrected code, or step-by-step instructions. ALWAYS provide this field.
                - confidence   : a number from 0.0 to 1.0 indicating how certain you are this is a real issue (1.0 = certain, 0.7 = fairly confident, below 0.7 = speculative). Be honest — do not inflate confidence.

                Return your response as a JSON array of objects with this structure:
                [
                  {
                    "startLine": 42,
                    "endLine": 45,
                    "severity": "critical",
                    "type": "issue",
                    "comment": "Clear explanation of the problem",
                    "suggestedFix": "Concrete code snippet or step-by-step fix",
                    "confidence": 0.95
                  }
                ]

                If no issues are found, return an empty array: []
                """,
            name: "DotNetReviewAgent");
    }

    public async Task<List<CodeReviewComment>> ReviewFileAsync(
        PullRequestFile file,
        string codebaseContext,
        string? focusArea = null)
    {
        try
        {
            _logger.LogInformation("Reviewing .NET file: {FilePath} (focus: {Focus})", file.Path, focusArea ?? "all");

            var reviewSection = FocusPassHelper.GetReviewInstructions(focusArea) ?? """
                Provide a thorough code review focusing on:
                1. **Security Issues**: SQL injection, XSS, CSRF, insecure deserialization, hardcoded secrets
                2. **Bugs**: Null reference exceptions, race conditions, resource leaks, incorrect async usage
                3. **Performance**: Boxing/unboxing, string concatenation, excessive allocations, inefficient LINQ
                4. **Best Practices**: SOLID principles, proper disposal (IDisposable), exception handling, logging
                5. **Code Quality**: Naming conventions, method complexity, code duplication, accessibility modifiers
                6. **.NET-Specific**: Proper use of async/await, ConfigureAwait, CancellationToken, modern C# features
                """;

            var prompt = $$$"""
                Review ONLY THE CHANGES in the following C#/.NET file from a pull request.

                File Path: {{{file.Path}}}
                Change Type: {{{file.ChangeType}}}

                ========================================
                CHANGES TO REVIEW ('+' lines annotated with [Lxx] actual line numbers):
                ========================================
                ```diff
                {{{file.UnifiedDiff}}}
                ```

                ========================================
                FULL FILE CONTENT (FOR CONTEXT ONLY - DO NOT REVIEW):
                ========================================
                ```csharp
                {{{file.Content}}}
                ```

                {{{(string.IsNullOrEmpty(file.PreviousContent) ? "" : $@"
                ========================================
                PREVIOUS FILE CONTENT (FOR CONTEXT ONLY - DO NOT REVIEW):
                ========================================
                ```csharp
                {file.PreviousContent}
                ```
                ")}}}

                Codebase Context:
                {{{codebaseContext}}}

                {{{reviewSection}}}
                """;

            // Log LLM request details
            _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║ LLM REQUEST: DotNet Code Review Agent                      ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("📤 SENDING TO LLM:");
            _logger.LogInformation("   Agent: {AgentName}", "DotNetReviewAgent");
            _logger.LogInformation("   File: {FilePath}", file.Path);
            _logger.LogInformation("   Prompt length: {Length} chars", prompt.Length);
            _logger.LogInformation("   Diff length: {DiffLength} chars", file.UnifiedDiff?.Length ?? 0);
            _logger.LogInformation("   File content length: {ContentLength} chars", file.Content?.Length ?? 0);
            _logger.LogInformation("   Codebase context included: {HasContext}", !string.IsNullOrEmpty(codebaseContext));
            _logger.LogInformation("   Codebase context length: {ContextLength} chars", codebaseContext?.Length ?? 0);
            _logger.LogInformation("📝 FULL PROMPT:\n{Prompt}", prompt);

            // Use AIAgent.RunAsync to execute the agent
            var stopwatch = Stopwatch.StartNew();
            var response = await _agent.RunAsync(prompt);
            stopwatch.Stop();

            var responseText = response.Text;

            // Log LLM response details
            _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║ LLM RESPONSE: DotNet Code Review Agent                     ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("📥 RECEIVED FROM LLM:");
            _logger.LogInformation("   Response length: {Length} chars", responseText?.Length ?? 0);
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
                var inputCost = (response.Usage.InputTokenCount ?? 0) * 0.00003m;
                var outputCost = (response.Usage.OutputTokenCount ?? 0) * 0.00006m;
                _logger.LogInformation("   💰 Estimated cost: ${TotalCost:F4} (input: ${InputCost:F4}, output: ${OutputCost:F4})",
                    inputCost + outputCost, inputCost, outputCost);
            }
            else
            {
                _logger.LogInformation("📊 TOKEN USAGE: Not available from agent response");
            }

            _logger.LogInformation("📝 FULL LLM RESPONSE:\n{Response}", responseText);
            _logger.LogInformation("════════════════════════════════════════════════════════════");

            // Parse the JSON response
            var comments = ParseReviewComments(responseText ?? "[]", file.Path);

            _logger.LogInformation("✅ Found {CommentCount} review comments for {FilePath}",
                comments.Count, file.Path);

            return comments;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reviewing .NET file {FilePath}", file.Path);

            if (ReviewExceptionClassifier.IsAuthenticationError(ex))
            {
                throw new InvalidOperationException(
                    "LLM authentication failed during code review. Verify AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, and AZURE_OPENAI_DEPLOYMENT for the chat model.",
                    ex);
            }

            throw new InvalidOperationException($"Code review failed for file '{file.Path}'.", ex);
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
                StartLine = c.StartLine,
                EndLine = c.EndLine > 0 ? c.EndLine : c.StartLine,
                Severity = c.Severity?.ToLower() ?? "low",
                CommentType = c.Type?.ToLower() ?? "suggestion",
                CommentText = c.Comment ?? string.Empty,
                SuggestedFix = c.SuggestedFix ?? string.Empty,
                Confidence = c.Confidence is > 0.0 and <= 1.0 ? c.Confidence : 1.0
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

        var firstArray = trimmed.IndexOf('[');
        var lastArray = trimmed.LastIndexOf(']');
        if (firstArray >= 0 && lastArray > firstArray)
        {
            return trimmed.Substring(firstArray, lastArray - firstArray + 1);
        }

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
            // Let the caller attempt normal deserialize and surface parse failure in logs.
        }

        return trimmed;
    }

    private class ReviewCommentJson
    {
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string? Severity { get; set; }
        public string? Type { get; set; }
        public string? Comment { get; set; }
        public string? SuggestedFix { get; set; }
        public double Confidence { get; set; }
    }
}
