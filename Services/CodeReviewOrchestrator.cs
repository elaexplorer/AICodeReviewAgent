using System.ComponentModel;
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
                return new List<CodeReviewComment>();
            }
        });

        var results = await Task.WhenAll(reviewTasks);
        return results.SelectMany(comments => comments).ToList();
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
            var prompt = $$$"""
                You are a general code reviewer with broad knowledge of software engineering best practices.

                Review the following file that was changed in a pull request:

                File Path: {{{file.Path}}}
                Change Type: {{{file.ChangeType}}}

                Current Content:
                ```
                {{{file.Content}}}
                ```

                {{{(string.IsNullOrEmpty(file.PreviousContent) ? "" : $@"
                Previous Content:
                ```
                {file.PreviousContent}
                ```
                ")}}}

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

            ChatResponse response = await _chatClient.GetResponseAsync(messages);
            var responseText = response.Text ?? "[]";

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
            // Clean up the response
            jsonResponse = jsonResponse.Trim();
            if (jsonResponse.StartsWith("```json"))
                jsonResponse = jsonResponse.Substring(7);
            if (jsonResponse.StartsWith("```"))
                jsonResponse = jsonResponse.Substring(3);
            if (jsonResponse.EndsWith("```"))
                jsonResponse = jsonResponse.Substring(0, jsonResponse.Length - 3);
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
