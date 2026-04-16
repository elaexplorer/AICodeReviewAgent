using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodeReviewAgent.Services;

/// <summary>
/// Persists the latest ADO pipeline token (System.AccessToken) to Azure Blob Storage
/// so it survives container restarts and revision changes.
/// Falls back to in-memory only when AZURE_STORAGE_CONNECTION_STRING is not configured.
/// </summary>
public sealed class PipelineTokenStore
{
    private const string BlobContainerName = "code-review-tokens";
    private const string BlobName = "pipeline-token/latest.json";

    private readonly ILogger<PipelineTokenStore> _logger;
    private readonly string? _connectionString;

    private string? _token;
    private DateTime _receivedAt = DateTime.MinValue;
    private readonly object _lock = new();

    public string? Token { get { lock (_lock) return _token; } }
    public DateTime ReceivedAt { get { lock (_lock) return _receivedAt; } }

    public PipelineTokenStore(ILogger<PipelineTokenStore> logger)
    {
        _logger = logger;
        _connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(_connectionString))
            _logger.LogInformation("PipelineTokenStore: AZURE_STORAGE_CONNECTION_STRING not set — using in-memory only");
        else
            _logger.LogInformation("PipelineTokenStore: Azure Blob Storage configured for token persistence");
    }

    /// <summary>Updates the in-memory token and asynchronously persists it to blob storage.</summary>
    public void Set(string token)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            _token = token;
            _receivedAt = now;
        }

        if (!string.IsNullOrWhiteSpace(_connectionString))
            _ = SaveToBlobAsync(token, now);
    }

    /// <summary>Loads the pipeline token from Azure Blob Storage and populates in-memory state.</summary>
    public async Task RestoreFromBlobAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            return;

        try
        {
            var blobClient = GetBlobClient();
            if (!await blobClient.ExistsAsync())
            {
                _logger.LogInformation("PipelineTokenStore: No saved token blob found — starting fresh");
                return;
            }

            var download = await blobClient.DownloadContentAsync();
            var json = download.Value.Content.ToString();
            var record = JsonSerializer.Deserialize<TokenRecord>(json);

            if (record is null || string.IsNullOrWhiteSpace(record.Token))
            {
                _logger.LogWarning("PipelineTokenStore: Blob exists but contains no usable token");
                return;
            }

            var age = DateTime.UtcNow - record.ReceivedAt;
            if (age.TotalHours >= 24)
            {
                _logger.LogInformation(
                    "PipelineTokenStore: Stored token is {Age:F1}h old — too stale to restore (24h limit)",
                    age.TotalHours);
                return;
            }

            lock (_lock)
            {
                _token = record.Token;
                _receivedAt = record.ReceivedAt;
            }

            _logger.LogInformation(
                "PipelineTokenStore: Restored pipeline token from blob (stored {AgeMin}m ago)",
                (int)age.TotalMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PipelineTokenStore: Failed to restore token from blob — continuing in-memory only");
        }
    }

    private async Task SaveToBlobAsync(string token, DateTime receivedAt)
    {
        try
        {
            var containerClient = new BlobServiceClient(_connectionString)
                .GetBlobContainerClient(BlobContainerName);
            await containerClient.CreateIfNotExistsAsync();

            var json = JsonSerializer.Serialize(new TokenRecord(token, receivedAt));
            await containerClient.GetBlobClient(BlobName)
                .UploadAsync(BinaryData.FromString(json), overwrite: true);

            _logger.LogDebug("PipelineTokenStore: Token persisted to blob storage");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PipelineTokenStore: Failed to save token to blob — in-memory copy still available");
        }
    }

    private BlobClient GetBlobClient()
        => new BlobServiceClient(_connectionString)
            .GetBlobContainerClient(BlobContainerName)
            .GetBlobClient(BlobName);

    private record TokenRecord(string Token, DateTime ReceivedAt);
}
