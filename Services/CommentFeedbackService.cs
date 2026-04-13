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
    // Set to false when InitDb cannot open the DB; all public methods no-op in degraded mode.
    private bool _dbAvailable;

    public CommentFeedbackService(ILogger<CommentFeedbackService> logger)
    {
        // Prefer the persistent file share mount so data survives container restarts/redeployments.
        // Falls back to /tmp only for local development.
        var mountRoot = Environment.GetEnvironmentVariable("RagRuntime__TempReposRootPath");
        var dir = !string.IsNullOrWhiteSpace(mountRoot)
            ? Path.Combine(mountRoot, "code-review-agent")
            : Path.Combine(Path.GetTempPath(), "code-review-agent");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "feedback.db");
        _logger = logger;
        _logger.LogInformation("CommentFeedbackService: SQLite DB at {DbPath}", _dbPath);
        InitDb();
    }

    private void InitDb()
    {
        try
        {
            using var conn = OpenRaw();
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
                CREATE TABLE IF NOT EXISTS review_jobs (
                    job_id         TEXT    NOT NULL PRIMARY KEY,
                    pr_id          INTEGER NOT NULL,
                    pr_url         TEXT    NOT NULL,
                    project        TEXT    NOT NULL,
                    repository     TEXT    NOT NULL,
                    reviewed_at    TEXT    NOT NULL,
                    gpt_total      INTEGER NOT NULL DEFAULT 0,
                    gpt_high       INTEGER NOT NULL DEFAULT 0,
                    claude_total   INTEGER NOT NULL DEFAULT 0,
                    claude_high    INTEGER NOT NULL DEFAULT 0,
                    posted_count   INTEGER NOT NULL DEFAULT 0,
                    skipped_count  INTEGER NOT NULL DEFAULT 0,
                    model_tag      TEXT    NOT NULL DEFAULT ''
                );
                CREATE TABLE IF NOT EXISTS posted_threads (
                    thread_id    INTEGER NOT NULL,
                    job_id       TEXT    NOT NULL,
                    pr_id        INTEGER NOT NULL,
                    project      TEXT    NOT NULL,
                    repository   TEXT    NOT NULL,
                    file_path    TEXT    NOT NULL,
                    start_line   INTEGER NOT NULL,
                    severity     TEXT    NOT NULL,
                    comment_type TEXT    NOT NULL,
                    source_model TEXT    NOT NULL DEFAULT 'gpt',
                    posted_at    TEXT    NOT NULL,
                    PRIMARY KEY (thread_id, pr_id, project, repository)
                );
                CREATE INDEX IF NOT EXISTS idx_review_jobs_date ON review_jobs(reviewed_at);
                CREATE INDEX IF NOT EXISTS idx_posted_threads_pr ON posted_threads(pr_id, project, repository);
                """;
            cmd.ExecuteNonQuery();
            _dbAvailable = true;
            _logger.LogInformation("CommentFeedbackService: DB initialized at {DbPath}", _dbPath);
        }
        catch (Exception ex)
        {
            _dbAvailable = false;
            _logger.LogError(ex, "CommentFeedbackService: DB init failed; feedback metrics unavailable (degraded mode)");
        }
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
        if (!_dbAvailable) return;
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
        if (!_dbAvailable) return 0;
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

    // ─── Job journal ───────────────────────────────────────────────────────────

    public async Task RecordJobAsync(ReviewJobRecord job)
    {
        if (!_dbAvailable) return;
        await Task.Run(() =>
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO review_jobs
                    (job_id, pr_id, pr_url, project, repository, reviewed_at,
                     gpt_total, gpt_high, claude_total, claude_high,
                     posted_count, skipped_count, model_tag)
                VALUES
                    ($jid, $prid, $url, $proj, $repo, $ts,
                     $gpt_t, $gpt_h, $cl_t, $cl_h,
                     $posted, $skipped, $tag);
                """;
            cmd.Parameters.AddWithValue("$jid",     job.JobId);
            cmd.Parameters.AddWithValue("$prid",    job.PrId);
            cmd.Parameters.AddWithValue("$url",     job.PrUrl);
            cmd.Parameters.AddWithValue("$proj",    job.Project);
            cmd.Parameters.AddWithValue("$repo",    job.Repository);
            cmd.Parameters.AddWithValue("$ts",      job.ReviewedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$gpt_t",   job.GptTotal);
            cmd.Parameters.AddWithValue("$gpt_h",   job.GptHigh);
            cmd.Parameters.AddWithValue("$cl_t",    job.ClaudeTotal);
            cmd.Parameters.AddWithValue("$cl_h",    job.ClaudeHigh);
            cmd.Parameters.AddWithValue("$posted",  job.PostedCount);
            cmd.Parameters.AddWithValue("$skipped", job.SkippedCount);
            cmd.Parameters.AddWithValue("$tag",     job.ModelTag);
            cmd.ExecuteNonQuery();
            _logger.LogInformation("Recorded review job {JobId} for PR {PrId}", job.JobId, job.PrId);
        });
    }

    public async Task RecordPostedThreadsAsync(
        string jobId, int prId, string project, string repository,
        List<PostedThreadRecord> threads)
    {
        if (!_dbAvailable) return;
        await Task.Run(() =>
        {
            using var conn = Open();
            foreach (var t in threads)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO posted_threads
                        (thread_id, job_id, pr_id, project, repository,
                         file_path, start_line, severity, comment_type, source_model, posted_at)
                    VALUES
                        ($tid, $jid, $prid, $proj, $repo,
                         $fp, $sl, $sev, $ct, $src, $ts);
                    """;
                cmd.Parameters.AddWithValue("$tid",  t.ThreadId);
                cmd.Parameters.AddWithValue("$jid",  jobId);
                cmd.Parameters.AddWithValue("$prid", prId);
                cmd.Parameters.AddWithValue("$proj", project);
                cmd.Parameters.AddWithValue("$repo", repository);
                cmd.Parameters.AddWithValue("$fp",   t.FilePath);
                cmd.Parameters.AddWithValue("$sl",   t.StartLine);
                cmd.Parameters.AddWithValue("$sev",  t.Severity);
                cmd.Parameters.AddWithValue("$ct",   t.CommentType);
                cmd.Parameters.AddWithValue("$src",  t.SourceModel);
                cmd.Parameters.AddWithValue("$ts",   DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// Fetches all posted thread IDs for a PR, then syncs their current ADO status into the
    /// resolution table. Called from the daily-report endpoint before building the email.
    /// </summary>
    public async Task<int> SyncThreadStatusesForPrAsync(
        string project, string repository, int prId,
        Func<string, string, int, Task<List<AgentThreadStatus>>> fetchStatusesFromAdo)
    {
        if (!_dbAvailable) return 0;
        // Collect all thread IDs we posted for this PR
        var tracked = await Task.Run(() =>
        {
            var list = new List<AgentThreadStatus>();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT pt.thread_id, pt.thread_id AS comment_id_placeholder
                FROM posted_threads pt
                WHERE pt.pr_id=$prid AND pt.project=$proj AND pt.repository=$repo
                """;
            cmd.Parameters.AddWithValue("$prid", prId);
            cmd.Parameters.AddWithValue("$proj", project);
            cmd.Parameters.AddWithValue("$repo", repository);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AgentThreadStatus { ThreadId = r.GetInt32(0), CommentId = r.GetInt32(0).ToString(), Status = "active" });
            return list;
        });

        if (tracked.Count == 0) return 0;

        var liveStatuses = await fetchStatusesFromAdo(project, repository, prId);
        var liveById = liveStatuses.ToDictionary(s => s.ThreadId);

        var toSync = tracked
            .Where(t => liveById.ContainsKey(t.ThreadId))
            .Select(t => new AgentThreadStatus
            {
                ThreadId  = t.ThreadId,
                CommentId = t.ThreadId.ToString(),
                Status    = liveById[t.ThreadId].Status
            })
            .ToList();

        return await SyncResolutionsAsync(project, repository, prId, toSync);
    }

    public List<DailyReportRow> GetTodayJobs(DateTimeOffset? forDate = null)
    {
        if (!_dbAvailable) return [];
        var date = (forDate ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-dd");
        var rows = new List<DailyReportRow>();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT j.job_id, j.pr_id, j.pr_url, j.project, j.repository,
                   j.gpt_total, j.gpt_high, j.claude_total, j.claude_high,
                   j.posted_count, j.skipped_count, j.model_tag, j.reviewed_at,
                   COALESCE(SUM(CASE WHEN r.status='fixed'   THEN 1 ELSE 0 END),0) AS resolved,
                   COALESCE(SUM(CASE WHEN r.status IN ('wontFix','byDesign','closed') THEN 1 ELSE 0 END),0) AS rejected,
                   COALESCE(SUM(CASE WHEN r.status='active'  THEN 1 ELSE 0 END),0) AS active,
                   COUNT(pt.thread_id) AS total_threads
            FROM review_jobs j
            LEFT JOIN posted_threads pt ON pt.job_id=j.job_id
            LEFT JOIN resolution r ON r.thread_id=pt.thread_id
                AND r.project=j.project AND r.repository=j.repository
            WHERE j.reviewed_at LIKE $date
            GROUP BY j.job_id
            ORDER BY j.reviewed_at DESC;
            """;
        cmd.Parameters.AddWithValue("$date", $"{date}%");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new DailyReportRow
            {
                JobId        = reader.GetString(0),
                PrId         = reader.GetInt32(1),
                PrUrl        = reader.GetString(2),
                Project      = reader.GetString(3),
                Repository   = reader.GetString(4),
                GptTotal     = reader.GetInt32(5),
                GptHigh      = reader.GetInt32(6),
                ClaudeTotal  = reader.GetInt32(7),
                ClaudeHigh   = reader.GetInt32(8),
                PostedCount  = reader.GetInt32(9),
                SkippedCount = reader.GetInt32(10),
                ModelTag     = reader.GetString(11),
                ReviewedAt   = reader.GetString(12),
                Resolved     = reader.GetInt32(13),
                Rejected     = reader.GetInt32(14),
                Active       = reader.GetInt32(15),
                TotalThreads = reader.GetInt32(16),
            });
        }
        return rows;
    }

    public CommentFeedbackMetrics GetMetrics()
    {
        if (!_dbAvailable) return new CommentFeedbackMetrics();
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

    /// <summary>
    /// Returns distinct PRs that have posted threads where at least one thread is still in a
    /// non-terminal state (active/pending) OR has never been synced.
    /// Excludes PRs whose threads were all last synced within <paramref name="staleAfter"/>.
    /// </summary>
    public DashboardStats GetDashboardStats()
    {
        if (!_dbAvailable) return new DashboardStats();

        var stats = new DashboardStats();
        using var conn = Open();

        // Totals from review_jobs
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    COUNT(DISTINCT pr_id)  AS prs,
                    COALESCE(SUM(gpt_high),    0) AS gpt_critical,
                    COALESCE(SUM(claude_high), 0) AS claude_critical,
                    COALESCE(SUM(posted_count),0) AS total_posted
                FROM review_jobs;
                """;
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                stats.TotalPrsReviewed   = r.GetInt32(0);
                stats.GptCriticalPosted  = r.GetInt32(1);
                stats.ClaudeCriticalPosted = r.GetInt32(2);
                stats.TotalCommentsPosted = r.GetInt32(3);
            }
        }

        // Comments by model from posted_threads
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT source_model, COUNT(*) FROM posted_threads GROUP BY source_model;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var model = r.GetString(0);
                var cnt   = r.GetInt32(1);
                if (model == "gpt")     stats.GptCommentsPosted    = cnt;
                else if (model == "claude") stats.ClaudeCommentsPosted = cnt;
            }
        }

        // Severity breakdown of posted comments
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT severity, COUNT(*) FROM posted_threads GROUP BY severity;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                stats.SeverityBreakdown[r.GetString(0)] = r.GetInt32(1);
        }

        // Resolution status breakdown
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT status, COUNT(*) FROM resolution GROUP BY status;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                stats.ResolutionByStatus[r.GetString(0)] = r.GetInt32(1);
        }

        // Helpful feedback totals
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    COALESCE(SUM(CASE WHEN is_helpful=1 THEN 1 ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN is_helpful=0 THEN 1 ELSE 0 END),0)
                FROM feedback;
                """;
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                stats.HelpfulFeedback    = r.GetInt32(0);
                stats.NotHelpfulFeedback = r.GetInt32(1);
            }
        }

        // PRs reviewed per day (last 30 days)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DATE(reviewed_at) AS day, COUNT(DISTINCT pr_id) AS cnt
                FROM review_jobs
                WHERE reviewed_at >= datetime('now','-30 days')
                GROUP BY day
                ORDER BY day;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                stats.PrsPerDay.Add(new DailyCount { Day = r.GetString(0), Count = r.GetInt32(1) });
        }

        // Recent PRs (last 30 reviews)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT j.pr_id, j.pr_url, j.repository, j.reviewed_at,
                       j.gpt_high, j.claude_high, j.posted_count,
                       COALESCE(SUM(CASE WHEN r.status='fixed'   THEN 1 ELSE 0 END),0) AS accepted,
                       COALESCE(SUM(CASE WHEN r.status IN ('wontFix','byDesign','closed') THEN 1 ELSE 0 END),0) AS rejected,
                       COALESCE(SUM(CASE WHEN r.status='active'  THEN 1 ELSE 0 END),0) AS still_open
                FROM review_jobs j
                LEFT JOIN posted_threads pt ON pt.job_id=j.job_id
                LEFT JOIN resolution r ON r.thread_id=pt.thread_id
                    AND r.project=j.project AND r.repository=j.repository
                GROUP BY j.job_id
                ORDER BY j.reviewed_at DESC
                LIMIT 30;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                stats.RecentPrs.Add(new RecentPrRow
                {
                    PrId        = r.GetInt32(0),
                    PrUrl       = r.GetString(1),
                    Repository  = r.GetString(2),
                    ReviewedAt  = r.GetString(3),
                    GptCritical = r.GetInt32(4),
                    ClaudeCritical = r.GetInt32(5),
                    PostedCount = r.GetInt32(6),
                    Accepted    = r.GetInt32(7),
                    Rejected    = r.GetInt32(8),
                    StillOpen   = r.GetInt32(9),
                });
            }
        }

        return stats;
    }

    public List<(string Project, string Repository, int PrId)> GetPrsNeedingSync(TimeSpan staleAfter)
    {
        if (!_dbAvailable) return [];
        var cutoff = DateTime.UtcNow.Subtract(staleAfter).ToString("o");
        var results = new List<(string, string, int)>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT pt.project, pt.repository, pt.pr_id
            FROM posted_threads pt
            WHERE EXISTS (
                SELECT 1 FROM posted_threads pt2
                WHERE pt2.pr_id=pt.pr_id AND pt2.project=pt.project AND pt2.repository=pt.repository
                  AND (
                    -- thread has never been synced
                    NOT EXISTS (SELECT 1 FROM resolution r
                                WHERE r.thread_id=pt2.thread_id
                                  AND r.project=pt2.project AND r.repository=pt2.repository)
                    OR
                    -- thread is in a non-terminal state and stale
                    EXISTS (SELECT 1 FROM resolution r
                            WHERE r.thread_id=pt2.thread_id
                              AND r.project=pt2.project AND r.repository=pt2.repository
                              AND r.status IN ('active','pending')
                              AND r.synced_at < $cutoff)
                  )
            )
            ORDER BY pt.pr_id DESC
            LIMIT 200;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return results;
    }

    private SqliteConnection Open()
    {
        if (!_dbAvailable)
            throw new InvalidOperationException("CommentFeedbackService is in degraded mode — DB unavailable");
        return OpenRaw();
    }

    /// <summary>
    /// Opens a SQLite connection without the _dbAvailable guard.
    /// Used by InitDb() before _dbAvailable is set to true.
    /// </summary>
    private SqliteConnection OpenRaw()
    {
        // Azure Files (SMB) on Linux does not support the POSIX fcntl() advisory locks that
        // SQLite uses by default.  Every lock acquisition blocks for busy_timeout ms and then
        // fails with SQLITE_BUSY.  Using nolock=1 disables SQLite's file-locking protocol
        // entirely — safe here because we only ever have ONE container replica writing to this DB.
        // Pooling=false ensures each Dispose() truly closes the underlying file handle instead
        // of returning it to the connection pool (which would keep stale handles open).
        var connStr = $"Data Source=file://{_dbPath}?nolock=1;Pooling=false";
        var conn = new SqliteConnection(connStr);
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

public class ReviewJobRecord
{
    public string JobId        { get; set; } = string.Empty;
    public int    PrId         { get; set; }
    public string PrUrl        { get; set; } = string.Empty;
    public string Project      { get; set; } = string.Empty;
    public string Repository   { get; set; } = string.Empty;
    public DateTime ReviewedAt { get; set; } = DateTime.UtcNow;
    public int    GptTotal     { get; set; }
    public int    GptHigh      { get; set; }
    public int    ClaudeTotal  { get; set; }
    public int    ClaudeHigh   { get; set; }
    public int    PostedCount  { get; set; }
    public int    SkippedCount { get; set; }
    public string ModelTag     { get; set; } = string.Empty;
}

public class PostedThreadRecord
{
    public int    ThreadId    { get; set; }
    public string FilePath    { get; set; } = string.Empty;
    public int    StartLine   { get; set; }
    public string Severity    { get; set; } = string.Empty;
    public string CommentType { get; set; } = string.Empty;
    public string SourceModel { get; set; } = "gpt";
}

public class DailyReportRow
{
    public string JobId        { get; set; } = string.Empty;
    public int    PrId         { get; set; }
    public string PrUrl        { get; set; } = string.Empty;
    public string Project      { get; set; } = string.Empty;
    public string Repository   { get; set; } = string.Empty;
    public int    GptTotal     { get; set; }
    public int    GptHigh      { get; set; }
    public int    ClaudeTotal  { get; set; }
    public int    ClaudeHigh   { get; set; }
    public int    PostedCount  { get; set; }
    public int    SkippedCount { get; set; }
    public string ModelTag     { get; set; } = string.Empty;
    public string ReviewedAt   { get; set; } = string.Empty;
    public int    Resolved     { get; set; }
    public int    Rejected     { get; set; }
    public int    Active       { get; set; }
    public int    TotalThreads { get; set; }
}

public class DashboardStats
{
    public int TotalPrsReviewed      { get; set; }
    public int TotalCommentsPosted   { get; set; }
    public int GptCommentsPosted     { get; set; }
    public int ClaudeCommentsPosted  { get; set; }
    public int GptCriticalPosted     { get; set; }
    public int ClaudeCriticalPosted  { get; set; }
    public int HelpfulFeedback       { get; set; }
    public int NotHelpfulFeedback    { get; set; }
    public Dictionary<string, int> ResolutionByStatus { get; set; } = new();
    public Dictionary<string, int> SeverityBreakdown  { get; set; } = new();
    public List<DailyCount>  PrsPerDay  { get; set; } = new();
    public List<RecentPrRow> RecentPrs  { get; set; } = new();
}

public class DailyCount
{
    public string Day   { get; set; } = string.Empty;
    public int    Count { get; set; }
}

public class RecentPrRow
{
    public int    PrId          { get; set; }
    public string PrUrl         { get; set; } = string.Empty;
    public string Repository    { get; set; } = string.Empty;
    public string ReviewedAt    { get; set; } = string.Empty;
    public int    GptCritical   { get; set; }
    public int    ClaudeCritical { get; set; }
    public int    PostedCount   { get; set; }
    public int    Accepted      { get; set; }
    public int    Rejected      { get; set; }
    public int    StillOpen     { get; set; }
}
