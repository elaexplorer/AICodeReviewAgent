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
    public int LineNumber { get; set; }
    public string CommentText { get; set; } = string.Empty;
    public string CommentType { get; set; } = string.Empty; // "suggestion", "issue", "nitpick"
    public string Severity { get; set; } = string.Empty; // "low", "medium", "high"
    public bool Posted { get; set; } = false;
}

public class RepositoryInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}