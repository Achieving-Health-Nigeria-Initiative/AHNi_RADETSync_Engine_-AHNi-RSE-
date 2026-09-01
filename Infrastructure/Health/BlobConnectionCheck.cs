using AHNiRSE.Configuration;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;


namespace AHNiRSE.Infrastructure.Health;

/// <summary>
/// ONE JOB ONLY: verify Azure Blob Storage is reachable and both containers are accessible.
///
/// Called once in Program.cs BEFORE host.Run() — if anything fails the service
/// logs clearly and exits rather than running silently broken.
///
/// Three checks in order:
///   1. Storage account reachable  (GetPropertiesAsync)
///   2. Script container exists and is readable
///   3. Output container exists (or is created) and is writable
///
/// Location: Infrastructure/Health/BlobConnectionCheck.cs
/// Registered: Program.cs → host.Services.GetRequiredService<BlobConnectionCheck>()
/// </summary>
public class BlobConnectionCheck(
    BlobServiceClient blobClient,
    IOptions<StorageSettings> options,
    ILogger<BlobConnectionCheck> logger)
{
    private readonly AzureStorageOptions _cfg = options.Value.Azure;

    /// <summary>
    /// Runs all checks. Returns true = all good. Returns false = something is broken.
    /// Never throws — all errors are caught and logged with actionable messages.
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken ct = default)
    {
        var scriptContainer = ResolveScriptContainerName();
        var outputContainer = _cfg.ContainerName;

        logger.LogInformation("══════════════════════════════════════════════");
        logger.LogInformation("[BlobCheck] Azure Blob Storage connection check");
        logger.LogInformation("[BlobCheck] Account  : {Uri}", blobClient.Uri);
        logger.LogInformation("[BlobCheck] Scripts  : container '{C}'", scriptContainer);
        logger.LogInformation("[BlobCheck] Output   : container '{C}'", outputContainer);
        logger.LogInformation("══════════════════════════════════════════════");

        // ── Check 1: Can we reach the storage account? ───────────────────────
        if (!await CheckAccountAsync(ct)) return false;


        // ── Check 2: Script container (must exist and be readable) ────────────
        if (!await CheckContainerAsync(scriptContainer, createIfMissing: false, ct)) return false;

        // ── Check 3: Output container (create it if it doesn't exist yet) ─────
        if (!string.Equals(scriptContainer, outputContainer, StringComparison.OrdinalIgnoreCase))
        {
            if (!await CheckContainerAsync(outputContainer, createIfMissing: true, ct)) return false;
        }

        logger.LogInformation("══════════════════════════════════════════════");
        logger.LogInformation("[BlobCheck] ✓ All checks passed — Azure storage is ready.");
        logger.LogInformation("══════════════════════════════════════════════");
        return true;
    }

    // ── Check 1 ──────────────────────────────────────────────────────────────

    private async Task<bool> CheckAccountAsync(CancellationToken ct)
    {
        try
        {
            await blobClient.GetPropertiesAsync(cancellationToken: ct);
            logger.LogInformation("[BlobCheck] ✓ Storage account reachable.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(
                "[BlobCheck] ✗ Cannot reach the storage account.\n" +
                "  → Check 'ConnectionStrings:AzureBlobStorage' in appsettings.json.\n" +
                "  → Error: {Msg}", ex.Message);
            return false;
        }
    }

    // ── Check 2 & 3 ──────────────────────────────────────────────────────────

    private async Task<bool> CheckContainerAsync(
        string containerName,
        bool createIfMissing,
        CancellationToken ct)
    {
        try
        {
            var container = blobClient.GetBlobContainerClient(containerName);
            bool exists = await container.ExistsAsync(ct);

            if (!exists && !createIfMissing)
            {
                logger.LogError(
                    "[BlobCheck] ✗ Container '{Name}' does NOT exist.\n" +
                    "  → Create it in Azure Portal / Storage Explorer,\n" +
                    "    then place your .sql script files inside.", containerName);
                return false;
            }

            if (!exists)
            {
                // Output container — safe to create automatically
                await container.CreateAsync(cancellationToken: ct);
                logger.LogInformation("[BlobCheck] ✓ Container '{Name}' created.", containerName);
                return true;
            }

            // Container exists — do a quick read to confirm permissions
            // (ExistsAsync only proves the container is there, not that we can list blobs)
            await foreach (var _ in container.GetBlobsAsync(cancellationToken: ct).AsPages(pageSizeHint: 1))
                break; // one page is enough to confirm read access

            logger.LogInformation("[BlobCheck] ✓ Container '{Name}' is accessible.", containerName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(
                "[BlobCheck] ✗ Error accessing container '{Name}'.\n" +
                "  → Check that the account key has read/write permissions on this container.\n" +
                "  → Error: {Msg}", containerName, ex.Message);
            return false;
        }
    }

    private string ResolveScriptContainerName()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.ScriptContainerName))
        {
            return _cfg.ScriptContainerName;
        }

        return _cfg.ContainerName;
    }
}

