using System.ComponentModel;
using CodeReviewAgent.Models;
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
                3. Provide your response as a JSON array of review comments

                For each issue found, provide:
                - Line number from the diff (lines marked with + are the new code)
                - Severity (high/medium/low)
                - Type (issue/suggestion/nitpick)
                - Clear explanation of the problem
                - Specific recommendation for fixing it

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
                CHANGES TO REVIEW (lines with '+' prefix):
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

            // Use AIAgent.RunAsync to execute the agent
            var response = await _agent.RunAsync(prompt);
            var responseText = response.Text;

            // Parse the JSON response
            var comments = ParseReviewComments(responseText, file.Path);

            _logger.LogInformation("Found {CommentCount} review comments for {FilePath}",
                comments.Count, file.Path);

            return comments;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reviewing Python file {FilePath}", file.Path);
            return new List<CodeReviewComment>();
        }
    }

    private List<CodeReviewComment> ParseReviewComments(string jsonResponse, string filePath)
    {
        try
        {
            // Clean up the response - remove markdown code blocks if present
            jsonResponse = jsonResponse.Trim();
            if (jsonResponse.StartsWith("```json"))
            {
                jsonResponse = jsonResponse.Substring(7);
            }
            if (jsonResponse.StartsWith("```"))
            {
                jsonResponse = jsonResponse.Substring(3);
            }
            if (jsonResponse.EndsWith("```"))
            {
                jsonResponse = jsonResponse.Substring(0, jsonResponse.Length - 3);
            }
            jsonResponse = jsonResponse.Trim();

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
            _logger.LogError(ex, "Error parsing review comments JSON");
            return new List<CodeReviewComment>();
        }
    }

    private class ReviewCommentJson
    {
        public int LineNumber { get; set; }
        public string? Severity { get; set; }
        public string? Type { get; set; }
        public string? Comment { get; set; }
    }
}
