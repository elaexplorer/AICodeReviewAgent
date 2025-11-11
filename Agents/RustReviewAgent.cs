using System.ComponentModel;
using CodeReviewAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace CodeReviewAgent.Agents;

/// <summary>
/// Rust code review expert agent
/// </summary>
public class RustReviewAgent : ILanguageReviewAgent
{
    private readonly ILogger<RustReviewAgent> _logger;
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chatService;

    public string Language => "Rust";
    public string[] FileExtensions => new[] { ".rs", ".toml" };

    public RustReviewAgent(
        ILogger<RustReviewAgent> logger,
        Kernel kernel)
    {
        _logger = logger;
        _kernel = kernel;
        _chatService = kernel.GetRequiredService<IChatCompletionService>();
    }

    [KernelFunction, Description("Review Rust code for issues, best practices, and potential bugs")]
    public async Task<List<CodeReviewComment>> ReviewFileAsync(
        [Description("The file to review")] PullRequestFile file,
        [Description("Context about the codebase structure")] string codebaseContext)
    {
        try
        {
            _logger.LogInformation("Reviewing Rust file: {FilePath}", file.Path);

            var prompt = $$$"""
                You are an expert Rust code reviewer with deep knowledge of:
                - Rust best practices and idioms
                - Ownership, borrowing, and lifetime rules
                - Memory safety and zero-cost abstractions
                - Common Rust security patterns
                - Performance optimization techniques
                - Error handling (Result, Option, panic)
                - Concurrency and thread safety (Send, Sync)
                - Popular Rust frameworks (tokio, actix, serde, etc.)
                - Cargo and dependency management

                Review ONLY THE CHANGES in the following Rust file from a pull request.

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
                ```rust
                {{{file.Content}}}
                ```

                {{{(string.IsNullOrEmpty(file.PreviousContent) ? "" : $@"
                ========================================
                PREVIOUS FILE CONTENT (FOR CONTEXT ONLY - DO NOT REVIEW):
                ========================================
                ```rust
                {file.PreviousContent}
                ```
                ")}}}

                Codebase Context:
                {{{codebaseContext}}}

                Provide a thorough code review focusing on:
                1. **Security Issues**: Unsafe code blocks, buffer overflows, integer overflows, race conditions
                2. **Memory Safety**: Improper use of unsafe, lifetime issues, use-after-free potential
                3. **Bugs**: Logic errors, panic conditions, incorrect error handling, unwrap usage
                4. **Performance**: Unnecessary cloning, inefficient algorithms, blocking operations
                5. **Best Practices**: Proper error propagation, idiomatic Rust patterns, documentation
                6. **Rust-Specific**: Ownership patterns, trait implementations, lifetime annotations, macro usage

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
            _logger.LogError(ex, "Error reviewing Rust file {FilePath}", file.Path);
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
