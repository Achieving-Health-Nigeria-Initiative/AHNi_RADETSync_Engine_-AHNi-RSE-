using AHNiRSE.Configuration;
using AHNiRSE.Core.Interfaces;
using AHNiRSE.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Infrastructure.Storage;

/// <summary>
/// Reads and writes files on the local filesystem.
/// Active when StorageSettings.Provider == "Local".
///
/// DownloadAsync copies from ScriptPath.
/// UploadAsync copies to OutputPath and creates directories as needed.
/// </summary>
public class LocalStorageProvider(
    IOptions<StorageSettings> options,
    ILogger<LocalStorageProvider> logger) : IStorageProvider
{
    private readonly LocalStorageOptions _cfg = options.Value.Local;

    public Task<string> DownloadAsync(string sourcePath, CancellationToken ct = default)
    {
        // sourcePath is a relative filename like "radet.sql"
        var fullPath = Path.Combine(_cfg.ScriptPath, sourcePath);

        logger.LogInformation("[LocalStorage] Download: {Source} â†’ {Dest}", sourcePath, fullPath);

        // Ensure script path exists
        if (!Directory.Exists(_cfg.ScriptPath))
        {
            logger.LogWarning("[LocalStorage] Script path does not exist: {Path}. Creating it now.", _cfg.ScriptPath);
            Directory.CreateDirectory(_cfg.ScriptPath);
        }

        return Task.FromResult(fullPath);
    }

    public async Task<string> UploadAsync(string localFilePath, string destinationPath, CancellationToken ct = default)
    {
        try
        {
            var dest = Path.Combine(_cfg.OutputPath, destinationPath);
            var destDir = Path.GetDirectoryName(dest);

            logger.LogInformation("[LocalStorage] Upload: {Source} â†’ {Dest}", localFilePath, dest);

            // Create destination directory if it doesn't exist
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
                logger.LogInformation("[LocalStorage] Created directory: {Path}", destDir);
            }

            // Copy file from temp location to output location
            if (File.Exists(localFilePath))
            {
                File.Copy(localFilePath, dest, overwrite: true);
                var fileSize = new FileInfo(dest).Length;
                logger.LogInformation("[LocalStorage] File copied successfully. Size: {Size} bytes", fileSize);
            }
            else
            {
                throw new FileNotFoundException($"Source file not found: {localFilePath}");
            }

            return await Task.FromResult(dest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LocalStorage] Upload failed: {Message}", ex.Message);
            throw new StorageException($"Failed to upload file: {destinationPath}", ex);
        }
    }

    public async Task<bool> DeleteAsync(string fileName, CancellationToken ct = default)
    {
        try
        {
            var filePath = Path.Combine(_cfg.OutputPath, fileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                logger.LogInformation("[LocalStorage] File deleted: {FileName}", fileName);
            }
            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LocalStorage] Error deleting file: {FileName}", fileName);
            return false;
        }
    }

    public async Task<IEnumerable<string>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
    {
        try
        {
            if (!Directory.Exists(_cfg.OutputPath))
                return [];

            var files = Directory.GetFiles(_cfg.OutputPath, prefix != null ? $"{prefix}*" : "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .Cast<string>()
                .ToList();

            logger.LogInformation("[LocalStorage] Listed {Count} file(s)", files.Count);
            return files;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LocalStorage] Error listing files");
            return [];
        }
    }
}

