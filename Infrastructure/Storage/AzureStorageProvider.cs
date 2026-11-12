using AHNiRSE.Configuration;
using AHNiRSE.Core.Interfaces;
using AHNiRSE.Shared;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Infrastructure.Storage;

public class AzureStorageProvider(
    BlobServiceClient blobClient,
    IOptions<StorageSettings> options,
    IHostEnvironment env,
    ILogger<AzureStorageProvider> logger) : IStorageProvider
{
    private readonly AzureStorageOptions _cfg = options.Value.Azure;

    public Task<string> DownloadAsync(string sourcePath, CancellationToken ct = default)
    {
        return DownloadWithFallbackAsync(sourcePath, ct);
    }

    private async Task<string> DownloadWithFallbackAsync(string sourcePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new StorageException("Download source path cannot be empty.");
        }

        var normalizedSourcePath = sourcePath.Replace('\\', '/').TrimStart('/');
        var scriptContainerName = ResolveScriptContainerName();
        var fallbackLocalPath = ResolveLocalScriptPath(normalizedSourcePath);
        var stagedPath = ResolveStagingScriptPath(scriptContainerName, normalizedSourcePath);
        var candidateBlobNames = BuildBlobCandidates(normalizedSourcePath);

        var timeoutPerAttempt = TimeSpan.FromSeconds(Math.Max(5, _cfg.DownloadTimeoutSeconds));
        var maxAttempts = Math.Max(1, _cfg.DownloadMaxRetryAttempts);
        var baseDelay = TimeSpan.FromSeconds(Math.Max(1, _cfg.DownloadRetryDelaySeconds));
        var container = blobClient.GetBlobContainerClient(scriptContainerName);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(timeoutPerAttempt);

            try
            {
                foreach (var blobName in candidateBlobNames)
                {
                    var blob = container.GetBlobClient(blobName);
                    var exists = await blob.ExistsAsync(attemptCts.Token);
                    if (!exists)
                    {
                        continue;
                    }

                    var stagedDir = Path.GetDirectoryName(stagedPath);
                    if (!string.IsNullOrWhiteSpace(stagedDir))
                    {
                        Directory.CreateDirectory(stagedDir);
                    }

                    await using var fs = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await blob.DownloadToAsync(fs, cancellationToken: attemptCts.Token);

                    logger.LogInformation(
                        "[AzureStorage] Script downloaded from blob '{Container}/{Blob}' -> {LocalPath}",
                        scriptContainerName,
                        blobName,
                        stagedPath);

                    return stagedPath;
                }

                logger.LogWarning(
                    "[AzureStorage] Script not found in blob container '{Container}' for path '{Path}'. Candidates: {Candidates}.",
                    scriptContainerName,
                    normalizedSourcePath,
                    string.Join(", ", candidateBlobNames));
                break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
            {
                var delay = CalculateBackoffWithJitter(baseDelay, attempt);
                logger.LogWarning(
                    "[AzureStorage] Script download timeout on attempt {Attempt}/{Max}. Retrying in {Delay}.",
                    attempt,
                    maxAttempts,
                    delay);
                await Task.Delay(delay, ct);
            }
            catch (RequestFailedException ex) when (IsTransientAzureError(ex) && attempt < maxAttempts)
            {
                var delay = CalculateBackoffWithJitter(baseDelay, attempt);
                logger.LogWarning(
                    ex,
                    "[AzureStorage] Transient script download error on attempt {Attempt}/{Max} (Status={Status}). Retrying in {Delay}.",
                    attempt,
                    maxAttempts,
                    ex.Status,
                    delay);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var delay = CalculateBackoffWithJitter(baseDelay, attempt);
                logger.LogWarning(
                    ex,
                    "[AzureStorage] Script download failed on attempt {Attempt}/{Max}. Retrying in {Delay}.",
                    attempt,
                    maxAttempts,
                    delay);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "[AzureStorage] Blob script download failed after {Attempt} attempt(s). Falling back to local script path.",
                    attempt);
                break;
            }
        }

        if (File.Exists(fallbackLocalPath))
        {
            logger.LogWarning(
                "[AzureStorage] Using local fallback script: {Path}. " +
                "Blob source was unavailable or missing in container '{Container}'.",
                fallbackLocalPath,
                scriptContainerName);
            return fallbackLocalPath;
        }

        throw new FileNotFoundException(
            $"SQL script '{normalizedSourcePath}' not found in blob container '{scriptContainerName}' " +
            $"and local fallback path '{fallbackLocalPath}'.",
            fallbackLocalPath);
    }

    public async Task<string> UploadAsync(
        string localFilePath,
        string destinationPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(localFilePath))
        {
            throw new FileNotFoundException($"File to upload not found: '{localFilePath}'.");
        }

        var (reportType, fileName) = SplitPath(destinationPath.Replace('\\', '/'));
        var blobPrefix = ResolveBlobFolder(_cfg.BlobPrefixes, reportType);
        var blobName = string.IsNullOrEmpty(blobPrefix) ? fileName : $"{blobPrefix}/{fileName}";

        var container = blobClient.GetBlobContainerClient(_cfg.ContainerName);
        var blob = container.GetBlobClient(blobName);
        var fileSizeBytes = new FileInfo(localFilePath).Length;

        var timeoutPerAttempt = TimeSpan.FromSeconds(Math.Max(5, _cfg.DownloadTimeoutSeconds));
        var maxAttempts = Math.Max(1, _cfg.DownloadMaxRetryAttempts);
        var baseDelay = TimeSpan.FromSeconds(Math.Max(1, _cfg.DownloadRetryDelaySeconds));

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(timeoutPerAttempt);

            try
            {
                logger.LogInformation(
                    "[AzureStorage] Upload attempt {Attempt}/{Max} -> {Container}/{Blob} ({SizeMb:F2} MB).",
                    attempt,
                    maxAttempts,
                    _cfg.ContainerName,
                    blobName,
                    fileSizeBytes / 1_048_576.0);

                await container.CreateIfNotExistsAsync(cancellationToken: attemptCts.Token);
                await using var fs = File.OpenRead(localFilePath);
                await blob.UploadAsync(fs, overwrite: true, cancellationToken: attemptCts.Token);

                logger.LogInformation("[AzureStorage] Upload complete -> {Uri}", blob.Uri);
                return blob.Uri.ToString();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
            {
                var delay = CalculateBackoffWithJitter(baseDelay, attempt);
                logger.LogWarning(
                    "[AzureStorage] Upload timeout on attempt {Attempt}/{Max}. Retrying in {Delay}.",
                    attempt,
                    maxAttempts,
                    delay);
                await Task.Delay(delay, ct);
            }
            catch (RequestFailedException ex) when (IsTransientAzureError(ex) && attempt < maxAttempts)
            {
                var delay = CalculateBackoffWithJitter(baseDelay, attempt);
                logger.LogWarning(
                    ex,
                    "[AzureStorage] Transient upload error on attempt {Attempt}/{Max} (Status={Status}). Retrying in {Delay}.",
                    attempt,
                    maxAttempts,
                    ex.Status,
                    delay);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var delay = CalculateBackoffWithJitter(baseDelay, attempt);
                logger.LogWarning(
                    ex,
                    "[AzureStorage] Upload failed on attempt {Attempt}/{Max}. Retrying in {Delay}.",
                    attempt,
                    maxAttempts,
                    delay);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                throw new StorageException(
                    $"Azure upload failed after {attempt} attempt(s) for blob '{blobName}'.",
                    ex);
            }
        }

        throw new StorageException($"Azure upload failed after {maxAttempts} attempt(s) for blob '{blobName}'.");
    }

    private string ResolveLocalScriptPath(string sourcePath)
    {
        var basePath = Path.IsPathRooted(_cfg.LocalScriptPath)
            ? _cfg.LocalScriptPath
            : Path.Combine(env.ContentRootPath, _cfg.LocalScriptPath);

        var nestedPath = Path.Combine(basePath, sourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(nestedPath))
        {
            return nestedPath;
        }

        var fileName = Path.GetFileName(sourcePath);
        return Path.Combine(basePath, fileName);
    }

    private string ResolveStagingScriptPath(string scriptContainerName, string sourcePath)
    {
        var stagingRoot = string.IsNullOrWhiteSpace(_cfg.TempDownloadPath)
            ? Path.Combine(Path.GetTempPath(), "AHNi-RSE", "scripts")
            : _cfg.TempDownloadPath;

        var relativePath = Path.Combine(
            scriptContainerName,
            sourcePath.Replace('/', Path.DirectorySeparatorChar));

        return Path.Combine(stagingRoot, relativePath);
    }

    private string ResolveScriptContainerName()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.ScriptContainerName))
        {
            return _cfg.ScriptContainerName;
        }

        return _cfg.ContainerName;
    }

    private static List<string> BuildBlobCandidates(string sourcePath)
    {
        var candidates = new List<string> { sourcePath };

        var fileName = Path.GetFileName(sourcePath);
        if (!string.IsNullOrWhiteSpace(fileName) &&
            !string.Equals(fileName, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(fileName);
        }

        return candidates;
    }

    private string ResolveBlobFolder(Dictionary<string, string> folderMap, string reportType)
    {
        if (string.IsNullOrWhiteSpace(reportType))
        {
            return string.Empty;
        }

        if (folderMap.TryGetValue(reportType.ToUpperInvariant(), out var folder))
        {
            return folder;
        }

        var fallback = reportType.ToLowerInvariant();
        logger.LogWarning(
            "[AzureStorage] No blob prefix configured for report type '{ReportType}'. Using fallback '{Fallback}'.",
            reportType,
            fallback);
        return fallback;
    }

    private static (string reportType, string fileName) SplitPath(string path)
    {
        var idx = path.IndexOf('/');
        return idx < 0
            ? (string.Empty, path)
            : (path[..idx].ToUpperInvariant(), path[(idx + 1)..]);
    }

    private static bool IsTransientAzureError(RequestFailedException ex)
    {
        return ex.Status is 408 or 429 or 500 or 502 or 503 or 504;
    }

    private static TimeSpan CalculateBackoffWithJitter(TimeSpan baseDelay, int attempt)
    {
        var exponent = Math.Max(0, attempt - 1);
        var delayMs = baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var jitterMultiplier = 0.85 + (Random.Shared.NextDouble() * 0.3);
        var jittered = delayMs * jitterMultiplier;
        return TimeSpan.FromMilliseconds(Math.Min(jittered, 30_000));
    }
}

