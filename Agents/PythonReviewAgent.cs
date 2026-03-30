using System.ComponentModel;
using System.Diagnostics;
using CodeReviewAgent.Models;
using CodeReviewAgent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

namespace CodeReviewAgent.Agents;

/// <summary>
/// Python code review expert agent using Microsoft.Agents.AI
/// </summary>
public class PythonReviewAgent : ILanguageReviewAgent
{
    private readonly ILogger<PythonReviewAgent> _logger;
    private readonly AIAgent _agent;

    public string Language => "Python";
    public string[] FileExtensions => new[] { ".py", ".pyw", ".pyi" };

    public PythonReviewAgent(
        ILogger<PythonReviewAgent> logger,
        IChatClient chatClient)
    {
        _logger = logger;

        // Create ChatClientAgent with specialized instructions
        _agent = new ChatClientAgent(
            chatClient,
            instructions: """
                You are an expert Python code reviewer with deep knowledge of:
                - Python best practices and PEP standards (PEP 8, PEP 20, PEP 484, etc.)
                - Common Python security vulnerabilities and patterns
                - Performance optimization techniques
                - Modern Python features (3.10+, async/await, type hints, dataclasses, etc.)
                - Popular Python frameworks (Django, Flask, FastAPI, pandas, numpy, etc.)
                - Testing patterns (pytest, unittest, mocking)

                CRITICAL RULES:
                1. ONLY comment on lines marked with '+' in the diff (new/modified lines)
                2. DO NOT comment on lines marked with '-' (removed lines) or context lines
                3. Each '+' line has an [Lxx] tag showing its actual line number in the file — e.g. +[L42] def foo():
                4. The 'lineNumber' in your response MUST be the exact number from the [Lxx] tag on the '+' line you are commenting on
                5. Provide your response as a JSON array of review comments

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
                - lineNumber    : The EXACT number from the [Lxx] tag on the '+' line containing the issue
                - severity      : one of critical/high/medium/low
                - type          : one of issue/suggestion/compliance/testing/nitpick
                - comment       : clear explanation of the problem
                - suggestedFix  : a concrete fix — a code snippet showing the corrected code, or step-by-step instructions. ALWAYS provide this field.

                Return your response as a JSON array of objects with this structure:
                [
                  {
                    "lineNumber": 42,
                    "severity": "critical",
                    "type": "issue",
                    "comment": "Clear explanation of the problem",
                    "suggestedFix": "Concrete code snippet or step-by-step fix"
                  }
                ]

                If no issues are found, return an empty array: []
                """,
            name: "PythonReviewAgent");
    }

    public async Task<List<CodeReviewComment>> ReviewFileAsync(
        PullRequestFile file,
        string codebaseContext)
    {
        try
        {
            _logger.LogInformation("Reviewing Python file: {FilePath}", file.Path);

            var prompt = $$$"""
                Review ONLY THE CHANGES in the following Python file from a pull request.

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
                ```python
                {{{file.Content}}}
                ```

                {{{(string.IsNullOrEmpty(file.PreviousContent) ? "" : $@"
                ========================================
                PREVIOUS FILE CONTENT (FOR CONTEXT ONLY - DO NOT REVIEW):
                ========================================
                ```python
                {file.PreviousContent}
                ```
                ")}}}

                Codebase Context:
                {{{codebaseContext}}}

                Provide a thorough code review focusing on:
                1. **Security Issues**: SQL injection, XSS, insecure deserialization, hardcoded secrets, path traversal
                2. **Bugs**: Logic errors, null/None handling, type mismatches, incorrect API usage
                3. **Performance**: Inefficient algorithms, unnecessary computations, memory leaks
                4. **Best Practices**: PEP compliance, proper error handling, logging, documentation
                5. **Code Quality**: Naming conventions, function complexity, code duplication
                6. **Python-Specific**: Proper use of context managers, generators, decorators, type hints
                """;

            // Log LLM request details
            _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║ LLM REQUEST: Python Code Review Agent                      ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("📤 SENDING TO LLM:");
            _logger.LogInformation("   Agent: {AgentName}", "PythonReviewAgent");
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
            _logger.LogInformation("║ LLM RESPONSE: Python Code Review Agent                     ║");
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
            _logger.LogError(ex, "Error reviewing Python file {FilePath}", file.Path);

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
                LineNumber = c.LineNumber,
                Severity = c.Severity?.ToLower() ?? "low",
                CommentType = c.Type?.ToLower() ?? "suggestion",
                CommentText = c.Comment ?? string.Empty,
                SuggestedFix = c.SuggestedFix ?? string.Empty
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
        public int LineNumber { get; set; }
        public string? Severity { get; set; }
        public string? Type { get; set; }
        public string? Comment { get; set; }
        public string? SuggestedFix { get; set; }
    }
}
