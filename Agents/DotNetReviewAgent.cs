using System.ComponentModel;
using CodeReviewAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;

namespace CodeReviewAgent.Agents;

/// <summary>
/// .NET/C# code review expert agent
/// </summary>
public class DotNetReviewAgent : ILanguageReviewAgent
{
    private readonly ILogger<DotNetReviewAgent> _logger;
    private readonly IChatClient _chatClient;

    public string Language => "DotNet";
    public string[] FileExtensions => new[] { ".cs", ".csproj", ".cshtml", ".razor" };

    public DotNetReviewAgent(
        ILogger<DotNetReviewAgent> logger,
        IChatClient chatClient)
    {
        _logger = logger;
        _chatClient = chatClient;
    }

    public async Task<List<CodeReviewComment>> ReviewFileAsync(
        PullRequestFile file,
        string codebaseContext)
    {
        try
        {
            _logger.LogInformation("Reviewing .NET file: {FilePath}", file.Path);

            var prompt = $$$"""
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

                Review ONLY THE CHANGES in the following C#/.NET file from a pull request.

                CRITICAL RULES:
                1. ONLY comment on lines marked with '+' in the diff below (new/modified lines)
                2. The full file contents are provided ONLY for understanding context
                3. DO NOT comment on any line that is not part of the diff changes
                4. DO NOT comment on lines marked with '-' (removed lines) or context lines (no prefix)
                5. You can reference existing code for context, but your comments must be about the NEW changes only

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

                Provide a thorough code review focusing on:
                1. **Security Issues**: SQL injection, XSS, CSRF, insecure deserialization, hardcoded secrets
                2. **Bugs**: Null reference exceptions, race conditions, resource leaks, incorrect async usage
                3. **Performance**: Boxing/unboxing, string concatenation, excessive allocations, inefficient LINQ
                4. **Best Practices**: SOLID principles, proper disposal (IDisposable), exception handling, logging
                5. **Code Quality**: Naming conventions, method complexity, code duplication, accessibility modifiers
                6. **.NET-Specific**: Proper use of async/await, ConfigureAwait, CancellationToken, modern C# features

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

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You are an expert C#/.NET code reviewer with comprehensive knowledge of best practices, security, and performance optimization."),
                new(ChatRole.User, prompt)
            };

            ChatResponse response = await _chatClient.GetResponseAsync(messages);
            var responseText = response.Text ?? "[]";

            // Parse the JSON response
            var comments = ParseReviewComments(responseText, file.Path);

            _logger.LogInformation("Found {CommentCount} review comments for {FilePath}",
                comments.Count, file.Path);

            return comments;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reviewing .NET file {FilePath}", file.Path);
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
