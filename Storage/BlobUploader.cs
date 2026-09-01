using AHNiRSE.Configuration;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Storage;

public class BlobUploader
{
    private readonly StorageSettings _settings;
    private readonly ILogger<BlobUploader> _logger;

    public BlobUploader(IOptions<StorageSettings> settings, ILogger<BlobUploader> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string> UploadCombinedReportAsync(
        string localFilePath,
        string filename,
        DateOnly reportDate,
        CancellationToken ct = default)
    {
        var blobPath = string.IsNullOrEmpty(_settings.ReportPrefix)
            ? $"{reportDate:yyyy-MM-dd}/{filename}"
            : $"{_settings.ReportPrefix}/{reportDate:yyyy-MM-dd}/{filename}";
        var fileSize = new FileInfo(localFilePath).Length;

        _logger.LogInformation(
            "Upload starting: {File} ({SizeMB:F1} MB) → {Path}",
            filename, fileSize / 1_048_576.0, blobPath);

        // NetworkTimeout = 1 hour — no timeout regardless of upload speed.
        // This is per-network-operation, not total upload time.
        var clientOptions = new BlobClientOptions();
        clientOptions.Retry.NetworkTimeout = TimeSpan.FromHours(1);
        clientOptions.Retry.MaxRetries = 5;
        clientOptions.Retry.Mode = RetryMode.Exponential;
        clientOptions.Retry.Delay = TimeSpan.FromSeconds(10);
        clientOptions.Retry.MaxDelay = TimeSpan.FromMinutes(2);

        var serviceClient = new BlobServiceClient(_settings.AzureBlobConnectionString, clientOptions);
        var containerClient = serviceClient.GetBlobContainerClient(_settings.ReportContainer);
        var blobClient = containerClient.GetBlobClient(blobPath);

        // Report progress every 5 MB
        long lastReported = 0;
        const long reportEvery = 5 * 1024 * 1024;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var progress = new Progress<long>(bytesUploaded =>
        {
            if (bytesUploaded - lastReported >= reportEvery || bytesUploaded == fileSize)
            {
                lastReported = bytesUploaded;
                double pct = bytesUploaded * 100.0 / fileSize;
                double kbps = bytesUploaded / 1024.0 / sw.Elapsed.TotalSeconds;
                _logger.LogInformation(
                    "Upload {Pct:F1}% ({MB:F1}/{TotalMB:F1} MB) — {Kbps:F0} KB/s",
                    pct,
                    bytesUploaded / 1_048_576.0,
                    fileSize / 1_048_576.0,
                    kbps);
            }
        });

        await using var stream = File.OpenRead(localFilePath);

        await blobClient.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            },
            ProgressHandler = progress,
            TransferOptions = new Azure.Storage.StorageTransferOptions
            {
                MaximumTransferSize = 4 * 1024 * 1024,   // 4 MB blocks
                MaximumConcurrency = 2,
                InitialTransferSize = 4 * 1024 * 1024
            }
        }, ct);

        sw.Stop();
        _logger.LogInformation(
            "Upload complete in {Elapsed:F1}s ({AvgKbps:F0} KB/s avg) — {Path}",
            sw.Elapsed.TotalSeconds,
            fileSize / 1024.0 / sw.Elapsed.TotalSeconds,
            blobPath);

        return blobPath;
    }
}