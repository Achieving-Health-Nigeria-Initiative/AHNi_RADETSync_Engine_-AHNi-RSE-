namespace AHNiRSE.Core.Interfaces;

/// <summary>
/// Abstraction over any file storage backend.
/// Implementations: LocalStorageProvider, AzureStorageProvider.
/// The active provider is chosen at startup via StorageSettings.Provider.
/// </summary>
public interface IStorageProvider
{
    /// <summary>Downloads a file from storage and returns its local temp path.</summary>
    Task<string> DownloadAsync(string sourcePath, CancellationToken ct = default);

    /// <summary>Uploads a local file to storage. Returns the final storage path.</summary>
    Task<string> UploadAsync(string localFilePath, string destinationPath, CancellationToken ct = default);
}
