using CodeReviewAgent.Models;

namespace CodeReviewAgent.Agents;

/// <summary>
/// Interface for language-specific code review agents
/// </summary>
public interface ILanguageReviewAgent
{
    /// <summary>
    /// The language this agent is expert in
    /// </summary>
    string Language { get; }

    /// <summary>
    /// File extensions supported by this agent
    /// </summary>
    string[] FileExtensions { get; }

    /// <summary>
    /// Review a single file and return code review comments
    /// </summary>
    Task<List<CodeReviewComment>> ReviewFileAsync(
        PullRequestFile file,
        string codebaseContext);
}
