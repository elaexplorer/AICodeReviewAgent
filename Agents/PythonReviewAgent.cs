using System.ComponentModel;
using CodeReviewAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace CodeReviewAgent.Agents;

/// <summary>
/// Python code review expert agent
/// </summary>
public class PythonReviewAgent : ILanguageReviewAgent
{
    private readonly ILogger<PythonReviewAgent> _logger;
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chatService;

    public string Language => "Python";
    public string[] FileExtensions => new[] { ".py", ".pyw", ".pyi" };

    public PythonReviewAgent(
        ILogger<PythonReviewAgent> logger,
        Kernel kernel)
    {
        _logger = logger;
        _kernel = kernel;
        _chatService = kernel.GetRequiredService<IChatCompletionService>();
    }

    [KernelFunction, Description("Review Python code for issues, best practices, and potential bugs")]
    public async Task<List<CodeReviewComment>> ReviewFileAsync(
        [Description("The file to review")] PullRequestFile file,
        [Description("Context about the codebase structure")] string codebaseContext)
    {
        try
        {
            _logger.LogInformation("Reviewing Python file: {FilePath}", file.Path);

            var prompt = $$$"""
                You are an expert Python code reviewer with deep knowledge of:
                - Python best practices and PEP standards (PEP 8, PEP 20, PEP 484, etc.)
                - Common Python security vulnerabilities and patterns
                - Performance optimization techniques
                - Modern Python features (3.10+, async/await, type hints, dataclasses, etc.)
                - Popular Python frameworks (Django, Flask, FastAPI, pandas, numpy, etc.)
                - Testing patterns (pytest, unittest, mocking)

                Review ONLY THE CHANGES in the following Python file from a pull request.

                IMPORTANT: Only review the lines that were ADDED or MODIFIED in this PR.
                Do NOT comment on existing code that wasn't changed. Lines starting with '+' are additions, lines starting with '-' are removals.

                File Path: {{{file.Path}}}
                Change Type: {{{file.ChangeType}}}

                Changes (Unified Diff):
                ```diff
                {{{file.UnifiedDiff}}}
                ```

                Codebase Context:
                {{{codebaseContext}}}

                Provide a thorough code review focusing on:
                1. **Security Issues**: SQL injection, XSS, insecure deserialization, hardcoded secrets, path traversal
                2. **Bugs**: Logic errors, null/None handling, type mismatches, incorrect API usage
                3. **Performance**: Inefficient algorithms, unnecessary computations, memory leaks
                4. **Best Practices**: PEP compliance, proper error handling, logging, documentation
                5. **Code Quality**: Naming conventions, function complexity, code duplication
                6. **Python-Specific**: Proper use of context managers, generators, decorators, type hints

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
                """;

            var response = await _chatService.GetChatMessageContentAsync(prompt);
            var responseText = response.Content ?? "[]";

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
