using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// Persists indexed embeddings to SQLite so they survive process restarts.
/// SQLite always uses local/ephemeral storage (never a network mount — SMB/NFS
/// don't support SQLite file locking). The DB is restored into the in-memory
/// store on startup via IndexRestoreHostedService.
/// </summary>
public class EmbeddingPersistenceService
{
    private readonly string _dbPath;
    private readonly ILogger<EmbeddingPersistenceService> _logger;
    private bool _available = true;

    /// <param name="dbDirectory">Hint for DB location — always overridden to a local path in container environments.</param>
    public EmbeddingPersistenceService(string dbDirectory, ILogger<EmbeddingPersistenceService> logger)
    {
        _logger = logger;

        // Always use local storage for SQLite — network mounts (Azure File Share / SMB)
        // don't support the file locking SQLite requires.
        var localDir = Path.Combine(Path.GetTempPath(), "code-review-agent");
        Directory.CreateDirectory(localDir);
        _dbPath = Path.Combine(localDir, "embeddings.db");

        try
        {
            EnsureSchema();
            _logger.LogInformation("📦 Embedding persistence DB: {DbPath}", _dbPath);
        }
        catch (Exception ex)
        {
            _available = false;
            _logger.LogWarning("⚠️ SQLite persistence unavailable ({Error}) — reviews will work but embeddings won't survive restarts.", ex.Message);
        }
    }

    // ─── Schema ────────────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Repositories (
                RepositoryId TEXT PRIMARY KEY,
                CommitHash   TEXT,
                IndexedAt    TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Chunks (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                RepositoryId TEXT    NOT NULL,
                FilePath     TEXT    NOT NULL,
                ChunkIndex   INTEGER NOT NULL,
                StartLine    INTEGER NOT NULL,
                EndLine      INTEGER NOT NULL,
                Content      TEXT    NOT NULL,
                Metadata     TEXT    NOT NULL,
                Embedding    BLOB    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_chunks_repo ON Chunks(RepositoryId);
            """;
        cmd.ExecuteNonQuery();
    }

    // ─── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Persist all chunks for a repository (replaces any prior data).
    /// </summary>
    public Task SaveAsync(string repositoryId, List<CodeChunk> chunks, string? commitHash = null) =>
        _available ? Task.Run(() => Save(repositoryId, chunks, commitHash)) : Task.CompletedTask;

    /// <summary>
    /// Load all chunks for a repository from SQLite. Returns null if not found.
    /// </summary>
    public Task<List<CodeChunk>?> LoadAsync(string repositoryId) =>
        _available ? Task.Run(() => Load(repositoryId)) : Task.FromResult<List<CodeChunk>?>(null);

    /// <summary>
    /// List all repository IDs that have been persisted.
    /// </summary>
    public Task<IReadOnlyList<string>> GetIndexedRepositoriesAsync() =>
        _available
            ? Task.Run<IReadOnlyList<string>>(GetIndexedRepositories)
            : Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>
    /// Return the commit hash stored for a repository, or null.
    /// </summary>
    public Task<string?> GetCommitHashAsync(string repositoryId) =>
        _available ? Task.Run(() => GetCommitHash(repositoryId)) : Task.FromResult<string?>(null);

    // ─── Sync implementations ──────────────────────────────────────────────────

    private void Save(string repositoryId, List<CodeChunk> chunks, string? commitHash)
    {
        _logger.LogInformation("💾 Persisting {Count} chunks for '{Repo}' to SQLite…", chunks.Count, repositoryId);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // Upsert repository row
        using (var upsertRepo = conn.CreateCommand())
        {
            upsertRepo.CommandText = """
                INSERT INTO Repositories (RepositoryId, CommitHash, IndexedAt)
                VALUES ($repo, $hash, $ts)
                ON CONFLICT(RepositoryId) DO UPDATE SET CommitHash=$hash, IndexedAt=$ts;
                """;
            upsertRepo.Parameters.AddWithValue("$repo", repositoryId);
            upsertRepo.Parameters.AddWithValue("$hash", (object?)commitHash ?? DBNull.Value);
            upsertRepo.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            upsertRepo.ExecuteNonQuery();
        }

        // Delete existing chunks for this repo
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM Chunks WHERE RepositoryId=$repo";
            del.Parameters.AddWithValue("$repo", repositoryId);
            del.ExecuteNonQuery();
        }

        // Insert all chunks
        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO Chunks (RepositoryId, FilePath, ChunkIndex, StartLine, EndLine, Content, Metadata, Embedding)
            VALUES ($repo, $fp, $ci, $sl, $el, $ct, $md, $emb)
            """;
        var pRepo = ins.Parameters.Add("$repo", SqliteType.Text);
        var pFp   = ins.Parameters.Add("$fp",   SqliteType.Text);
        var pCi   = ins.Parameters.Add("$ci",   SqliteType.Integer);
        var pSl   = ins.Parameters.Add("$sl",   SqliteType.Integer);
        var pEl   = ins.Parameters.Add("$el",   SqliteType.Integer);
        var pCt   = ins.Parameters.Add("$ct",   SqliteType.Text);
        var pMd   = ins.Parameters.Add("$md",   SqliteType.Text);
        var pEmb  = ins.Parameters.Add("$emb",  SqliteType.Blob);

        foreach (var chunk in chunks)
        {
            pRepo.Value = repositoryId;
            pFp.Value   = chunk.FilePath;
            pCi.Value   = chunk.ChunkIndex;
            pSl.Value   = chunk.StartLine;
            pEl.Value   = chunk.EndLine;
            pCt.Value   = chunk.Content;
            pMd.Value   = chunk.Metadata;
            pEmb.Value  = FloatsToBytes(chunk.Embedding);
            ins.ExecuteNonQuery();
        }

        tx.Commit();
        sw.Stop();
        _logger.LogInformation("✅ Persisted {Count} chunks in {Ms}ms", chunks.Count, sw.ElapsedMilliseconds);
    }

    private List<CodeChunk>? Load(string repositoryId)
    {
        using var conn = Open();

        // Check repo exists
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(1) FROM Repositories WHERE RepositoryId=$repo";
            check.Parameters.AddWithValue("$repo", repositoryId);
            var count = (long)(check.ExecuteScalar() ?? 0L);
            if (count == 0) return null;
        }

        _logger.LogInformation("📂 Loading persisted chunks for '{Repo}' from SQLite…", repositoryId);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var chunks = new List<CodeChunk>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FilePath, ChunkIndex, StartLine, EndLine, Content, Metadata, Embedding
            FROM Chunks WHERE RepositoryId=$repo ORDER BY FilePath, ChunkIndex
            """;
        cmd.Parameters.AddWithValue("$repo", repositoryId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var embBytes = (byte[])reader.GetValue(6);
            chunks.Add(new CodeChunk
            {
                FilePath   = reader.GetString(0),
                ChunkIndex = reader.GetInt32(1),
                StartLine  = reader.GetInt32(2),
                EndLine    = reader.GetInt32(3),
                Content    = reader.GetString(4),
                Metadata   = reader.GetString(5),
                Embedding  = BytesToFloats(embBytes)
            });
        }

        sw.Stop();
        _logger.LogInformation("✅ Loaded {Count} chunks in {Ms}ms", chunks.Count, sw.ElapsedMilliseconds);
        return chunks;
    }

    private IReadOnlyList<string> GetIndexedRepositories()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT RepositoryId FROM Repositories ORDER BY IndexedAt DESC";
        using var reader = cmd.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private string? GetCommitHash(string repositoryId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CommitHash FROM Repositories WHERE RepositoryId=$repo";
        cmd.Parameters.AddWithValue("$repo", repositoryId);
        var val = cmd.ExecuteScalar();
        return val is DBNull or null ? null : (string)val;
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        MemoryMarshal.AsBytes(floats.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(floats);
        return floats;
    }
}
