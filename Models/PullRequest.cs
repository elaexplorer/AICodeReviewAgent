using System.Text.Json.Serialization;

namespace CodeReviewAgent.Models;

public class PullRequest
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("sourceBranch")]
    public string SourceBranch { get; set; } = string.Empty;

    [JsonPropertyName("targetBranch")]
    public string TargetBranch { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("isDraft")]
    public bool IsDraft { get; set; } = false;

    [JsonPropertyName("createdBy")]
    public PullRequestUser CreatedBy { get; set; } = new();

    [JsonPropertyName("creationDate")]
    public DateTime CreationDate { get; set; }
}

public class PullRequestUser
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = string.Empty;
}

public class PullRequestFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("changeType")]
    public string ChangeType { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("previousContent")]
    public string PreviousContent { get; set; } = string.Empty;

    [JsonPropertyName("unifiedDiff")]
    public string UnifiedDiff { get; set; } = string.Empty;
}

public class CodeReviewComment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string CommentText { get; set; } = string.Empty;

    // "issue" | "suggestion" | "compliance" | "testing" | "nitpick"
    // compliance = PII logging, audit gaps, data-retention violations
    // testing    = missing coverage on critical paths
    public string CommentType { get; set; } = string.Empty;

    // "critical" | "high" | "medium" | "low"
    // critical = security vulnerability / data loss / crash — must fix before merge
    // high     = bug causing incorrect behaviour
    // medium   = performance issue or non-critical bug
    // low      = minor improvement
    public string Severity { get; set; } = string.Empty;

    // Concrete fix: code snippet or step-by-step instructions provided by the LLM
    public string SuggestedFix { get; set; } = string.Empty;

    // LLM confidence that this is a real issue (0.0–1.0). Comments below 0.7 are not posted.
    public double Confidence { get; set; } = 1.0;

    public bool   Posted   { get; set; } = false;
    public int?   ThreadId { get; set; }
}

public class RepositoryInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}