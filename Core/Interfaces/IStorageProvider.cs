namespace AHNiRSE.Core.Interfaces;

/// <summary>
/// Abstraction over the file storage backend (AzureStorageProvider): SQL scripts are
/// downloaded from the script container and finished workbooks uploaded to the output container.
/// </summary>
public interface IStorageProvider
{
    /// <summary>Downloads a file from storage and returns its local temp path.</summary>
    Task<string> DownloadAsync(string sourcePath, CancellationToken ct = default);

    /// <summary>
    /// Uploads a local file to storage. Returns the final storage path and the time the upload
    /// completed by the storage service's clock (not the facility PC's).
    /// </summary>
    Task<(string Url, DateTimeOffset UploadedAt)> UploadAsync(
        string localFilePath, string destinationPath, CancellationToken ct = default);
}
