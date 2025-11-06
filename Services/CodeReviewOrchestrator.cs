using System.ComponentModel;
using CodeReviewAgent.Agents;
using CodeReviewAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace CodeReviewAgent.Services;

/// <summary>
/// Orchestrates code review by routing files to appropriate language-specific agents
/// using Semantic Kernel's function calling capabilities
/// </summary>
public class CodeReviewOrchestrator
{
    private readonly ILogger<CodeReviewOrchestrator> _logger;
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chatService;
    private readonly Dictionary<string, ILanguageReviewAgent> _agents;
    private readonly Dictionary<string, string> _extensionToLanguage;

    public CodeReviewOrchestrator(
        ILogger<CodeReviewOrchestrator> logger,
        Kernel kernel,
        IEnumerable<ILanguageReviewAgent> agents)
    {
        _logger = logger;
        _kernel = kernel;
        _chatService = kernel.GetRequiredService<IChatCompletionService>();
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

        // Register agents as kernel plugins for function calling
        foreach (var agent in agents)
        {
            _kernel.Plugins.AddFromObject(agent, agent.Language);
        }

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
        var allComments = new List<CodeReviewComment>();

        foreach (var file in files)
        {
            try
            {
                var extension = Path.GetExtension(file.Path);

                if (string.IsNullOrEmpty(extension))
                {
                    _logger.LogWarning("Skipping file without extension: {FilePath}", file.Path);
                    continue;
                }

                // Determine which agent should review this file
                if (_extensionToLanguage.TryGetValue(extension, out var language) &&
                    _agents.TryGetValue(language, out var agent))
                {
                    _logger.LogInformation("Routing {FilePath} to {Language} agent", file.Path, language);

                    var comments = await agent.ReviewFileAsync(file, codebaseContext);
                    allComments.AddRange(comments);
                }
                else
                {
                    _logger.LogInformation("No specialized agent for {Extension}, using general review for {FilePath}",
                        extension, file.Path);

                    var comments = await ReviewWithGeneralAgentAsync(file, codebaseContext);
                    allComments.AddRange(comments);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reviewing file {FilePath}", file.Path);
            }
        }

        return allComments;
    }

    /// <summary>
    /// Use Semantic Kernel's function calling to automatically select and invoke the right agent
    /// </summary>
    public async Task<List<CodeReviewComment>> ReviewFilesWithAutoSelectionAsync(
        List<PullRequestFile> files,
        string codebaseContext)
    {
        var allComments = new List<CodeReviewComment>();

        foreach (var file in files)
        {
            try
            {
                _logger.LogInformation("Auto-selecting agent for file: {FilePath}", file.Path);

                // Create a prompt for the orchestrator to decide which agent to use
                var orchestrationPrompt = $$$"""
                    You are a code review orchestrator. You have access to specialized code review agents for different programming languages.

                    Available agents:
                    {{{string.Join("\n", _agents.Keys.Select(k => $"- {k}"))}}}

                    File to review: {{{file.Path}}}
                    File extension: {{{Path.GetExtension(file.Path)}}}

                    Based on the file path and extension, call the appropriate language-specific review agent function to review this file.

                    If no specialized agent matches, provide a general code review.
                    """;

                // Use automatic function calling to route to the right agent
                var executionSettings = new OpenAIPromptExecutionSettings
                {
                    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
                };

                var result = await _kernel.InvokePromptAsync(
                    orchestrationPrompt,
                    new KernelArguments(executionSettings)
                    {
                        ["file"] = file,
                        ["codebaseContext"] = codebaseContext
                    });

                _logger.LogInformation("Orchestration result for {FilePath}: {Result}",
                    file.Path, result.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in auto-selection orchestration for {FilePath}", file.Path);
            }
        }

        return allComments;
    }

    /// <summary>
    /// General review for files without specialized agents
    /// </summary>
    [KernelFunction, Description("Review any code file when no specialized language agent is available")]
    private async Task<List<CodeReviewComment>> ReviewWithGeneralAgentAsync(
        [Description("The file to review")] PullRequestFile file,
        [Description("Context about the codebase")] string codebaseContext)
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

            var response = await _chatService.GetChatMessageContentAsync(prompt);
            var responseText = response.Content ?? "[]";

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
