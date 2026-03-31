using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// Stores thumbs-up / thumbs-down feedback for posted review comments.
/// Backed by SQLite so metrics survive restarts.
/// </summary>
public class CommentFeedbackService
{
    private readonly string _dbPath;
    private readonly ILogger<CommentFeedbackService> _logger;

    public CommentFeedbackService(ILogger<CommentFeedbackService> logger)
    {
        var dir = Path.Combine(Path.GetTempPath(), "code-review-agent");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "feedback.db");
        _logger = logger;
        InitDb();
    }

    private void InitDb()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS feedback (
                comment_id   TEXT    NOT NULL,
                pr_id        INTEGER NOT NULL,
                project      TEXT    NOT NULL,
                repository   TEXT    NOT NULL,
                file_path    TEXT    NOT NULL,
                severity     TEXT    NOT NULL,
                comment_type TEXT    NOT NULL,
                is_helpful   INTEGER NOT NULL,   -- 1 = helpful, 0 = not helpful
                recorded_at  TEXT    NOT NULL,
                PRIMARY KEY (comment_id, is_helpful)
            );
            CREATE TABLE IF NOT EXISTS resolution (
                comment_id  TEXT NOT NULL PRIMARY KEY,
                pr_id       INTEGER NOT NULL,
                project     TEXT NOT NULL,
                repository  TEXT NOT NULL,
                thread_id   INTEGER NOT NULL,
                status      TEXT NOT NULL,   -- active | fixed | wontFix | byDesign | closed | pending
                synced_at   TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task RecordAsync(
        string commentId,
        int prId,
        string project,
        string repository,
        string filePath,
        string severity,
        string commentType,
        bool isHelpful)
    {
        await Task.Run(() =>
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO feedback
                    (comment_id, pr_id, project, repository, file_path, severity, comment_type, is_helpful, recorded_at)
                VALUES
                    ($cid, $prid, $proj, $repo, $fp, $sev, $ct, $helpful, $ts);
                """;
            cmd.Parameters.AddWithValue("$cid", commentId);
            cmd.Parameters.AddWithValue("$prid", prId);
            cmd.Parameters.AddWithValue("$proj", project);
            cmd.Parameters.AddWithValue("$repo", repository);
            cmd.Parameters.AddWithValue("$fp", filePath);
            cmd.Parameters.AddWithValue("$sev", severity);
            cmd.Parameters.AddWithValue("$ct", commentType);
            cmd.Parameters.AddWithValue("$helpful", isHelpful ? 1 : 0);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            _logger.LogInformation("Recorded feedback commentId={CommentId} helpful={Helpful}", commentId, isHelpful);
        });
    }

    /// <summary>
    /// Upserts ADO thread resolution status for each agent-posted comment.
    /// Called by the /feedback/sync endpoint after fetching thread statuses from ADO.
    /// </summary>
    public async Task<int> SyncResolutionsAsync(
        string project,
        string repository,
        int prId,
        List<AgentThreadStatus> statuses)
    {
        var count = 0;
        await Task.Run(() =>
        {
            using var conn = Open();
            foreach (var s in statuses)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO resolution
                        (comment_id, pr_id, project, repository, thread_id, status, synced_at)
                    VALUES
                        ($cid, $prid, $proj, $repo, $tid, $status, $ts);
                    """;
                cmd.Parameters.AddWithValue("$cid",    s.CommentId);
                cmd.Parameters.AddWithValue("$prid",   prId);
                cmd.Parameters.AddWithValue("$proj",   project);
                cmd.Parameters.AddWithValue("$repo",   repository);
                cmd.Parameters.AddWithValue("$tid",    s.ThreadId);
                cmd.Parameters.AddWithValue("$status", s.Status);
                cmd.Parameters.AddWithValue("$ts",     DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
                count++;
            }
            _logger.LogInformation("Synced {Count} thread resolutions for PR {PrId}", count, prId);
        });
        return count;
    }

    public CommentFeedbackMetrics GetMetrics()
    {
        using var conn = Open();

        var metrics = new CommentFeedbackMetrics();

        // Total counts
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    COUNT(*)                                                   AS total,
                    COALESCE(SUM(CASE WHEN is_helpful=1 THEN 1 ELSE 0 END),0) AS helpful,
                    COALESCE(SUM(CASE WHEN is_helpful=0 THEN 1 ELSE 0 END),0) AS not_helpful
                FROM feedback;
                """;
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                metrics.TotalFeedback   = r.GetInt32(0);
                metrics.HelpfulCount    = r.GetInt32(1);
                metrics.NotHelpfulCount = r.GetInt32(2);
            }
        }

        // By severity
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT severity,
                       COALESCE(SUM(CASE WHEN is_helpful=1 THEN 1 ELSE 0 END),0) AS helpful,
                       COALESCE(SUM(CASE WHEN is_helpful=0 THEN 1 ELSE 0 END),0) AS not_helpful
                FROM feedback
                GROUP BY severity;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                metrics.BySeverity[r.GetString(0)] = new FeedbackBucket
                {
                    Helpful    = r.GetInt32(1),
                    NotHelpful = r.GetInt32(2)
                };
            }
        }

        // By comment type
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT comment_type,
                       COALESCE(SUM(CASE WHEN is_helpful=1 THEN 1 ELSE 0 END),0) AS helpful,
                       COALESCE(SUM(CASE WHEN is_helpful=0 THEN 1 ELSE 0 END),0) AS not_helpful
                FROM feedback
                GROUP BY comment_type;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                metrics.ByCommentType[r.GetString(0)] = new FeedbackBucket
                {
                    Helpful    = r.GetInt32(1),
                    NotHelpful = r.GetInt32(2)
                };
            }
        }

        // Recent 20 entries
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT comment_id, pr_id, repository, file_path, severity, comment_type, is_helpful, recorded_at
                FROM feedback
                ORDER BY recorded_at DESC
                LIMIT 20;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                metrics.RecentFeedback.Add(new FeedbackEntry
                {
                    CommentId   = r.GetString(0),
                    PrId        = r.GetInt32(1),
                    Repository  = r.GetString(2),
                    FilePath    = r.GetString(3),
                    Severity    = r.GetString(4),
                    CommentType = r.GetString(5),
                    IsHelpful   = r.GetInt32(6) == 1,
                    RecordedAt  = r.GetString(7)
                });
            }
        }

        // Resolution breakdown (from ADO thread status syncs)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT status, COUNT(*) AS cnt
                FROM resolution
                GROUP BY status;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                metrics.ResolutionByStatus[r.GetString(0)] = r.GetInt32(1);
        }

        // Recent 10 resolutions
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT comment_id, pr_id, repository, thread_id, status, synced_at
                FROM resolution
                ORDER BY synced_at DESC
                LIMIT 10;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                metrics.RecentResolutions.Add(new ResolutionEntry
                {
                    CommentId  = r.GetString(0),
                    PrId       = r.GetInt32(1),
                    Repository = r.GetString(2),
                    ThreadId   = r.GetInt32(3),
                    Status     = r.GetString(4),
                    SyncedAt   = r.GetString(5)
                });
            }
        }

        return metrics;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}

public class CommentFeedbackMetrics
{
    public int TotalFeedback  { get; set; }
    public int HelpfulCount   { get; set; }
    public int NotHelpfulCount { get; set; }
    public double HelpfulPercent =>
        TotalFeedback > 0 ? Math.Round(HelpfulCount * 100.0 / TotalFeedback, 1) : 0;
    public Dictionary<string, FeedbackBucket> BySeverity    { get; set; } = new();
    public Dictionary<string, FeedbackBucket> ByCommentType { get; set; } = new();
    public List<FeedbackEntry> RecentFeedback { get; set; } = new();

    // ADO thread resolution (synced via /feedback/sync)
    // Keys: active | fixed | wontFix | byDesign | closed | pending
    public Dictionary<string, int> ResolutionByStatus { get; set; } = new();
    public int TotalResolved => ResolutionByStatus.TryGetValue("fixed", out var f) ? f : 0;
    public int TotalRejected =>
        (ResolutionByStatus.TryGetValue("wontFix", out var w)  ? w : 0) +
        (ResolutionByStatus.TryGetValue("byDesign", out var b) ? b : 0) +
        (ResolutionByStatus.TryGetValue("closed", out var c)   ? c : 0);
    public int TotalSynced  => ResolutionByStatus.Values.Sum();
    public double FixedPercent =>
        TotalSynced > 0 ? Math.Round(TotalResolved * 100.0 / TotalSynced, 1) : 0;
    public List<ResolutionEntry> RecentResolutions { get; set; } = new();
}

public class FeedbackBucket
{
    public int Helpful    { get; set; }
    public int NotHelpful { get; set; }
    public double HelpfulPercent =>
        (Helpful + NotHelpful) > 0 ? Math.Round(Helpful * 100.0 / (Helpful + NotHelpful), 1) : 0;
}

public class FeedbackEntry
{
    public string CommentId   { get; set; } = string.Empty;
    public int    PrId        { get; set; }
    public string Repository  { get; set; } = string.Empty;
    public string FilePath    { get; set; } = string.Empty;
    public string Severity    { get; set; } = string.Empty;
    public string CommentType { get; set; } = string.Empty;
    public bool   IsHelpful   { get; set; }
    public string RecordedAt  { get; set; } = string.Empty;
}

public class ResolutionEntry
{
    public string CommentId  { get; set; } = string.Empty;
    public int    PrId       { get; set; }
    public string Repository { get; set; } = string.Empty;
    public int    ThreadId   { get; set; }
    /// <summary>active | fixed | wontFix | byDesign | closed | pending</summary>
    public string Status     { get; set; } = string.Empty;
    public string SyncedAt   { get; set; } = string.Empty;
}
