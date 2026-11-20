// RsePublish - manages RSE auto-update releases in <container>/<folder> (default autoreport/rse-release).
//
//   publish  --exe <AHNiRSE.exe> --facilities <all | DATIM,DATIM,...> [--notes "..."] [--commit <sha>] [--by <name>]
//   promote  --facilities <all | DATIM,DATIM,...>     who should install the current release
//   pause | resume                                    stop / restart installs of the current release
//   rollback                                          make facilities go back to the previous release
//   status                                            show manifest.json
//
// Storage access, one of:
//   --secrets <release.secrets.json>   its AzureBlobStorage connection string (default ..\..\..\release.secrets.json)
//   --account <storage account name>   your own Azure sign-in (az login / Visual Studio / browser); needs the
//                                      "Storage Blob Data Contributor" role on the account
// Optional: --container autoreport  --folder rse-release
//
// Every manifest write is conditional on the manifest not having changed since it was read, so two people
// publishing at once can't overwrite each other; an exe that is already uploaded is never replaced.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AHNiRSE.Update;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("RsePublish <publish|promote|pause|resume|rollback|status> [--secrets <file> | --account <name>] ...");
    Console.WriteLine("Usually run through deploy\\Build-Release.ps1 -Publish and deploy\\Manage-Update.ps1; see tools/RsePublish/Program.cs.");
    return 2;
}

var command = args[0].ToLowerInvariant();
var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 1; i < args.Length; i++)
    if (args[i].StartsWith("--") && i + 1 < args.Length)
        opts[args[i][2..]] = args[++i];

string Opt(string name, string fallback = "") => opts.TryGetValue(name, out var v) ? v : fallback;

try
{
    var container = Connect().GetBlobContainerClient(Opt("container", "autoreport"));
    var folder = Opt("folder", "rse-release").Trim('/');
    var manifestBlob = container.GetBlobClient($"{folder}/manifest.json");
    var (current, etag) = await ReadManifestAsync(manifestBlob);

    switch (command)
    {
        case "status":
            Console.WriteLine(current is null
                ? $"No release published yet ({container.Name}/{folder}/manifest.json does not exist)."
                : JsonSerializer.Serialize(current, UpdateManifest.Json));
            return 0;

        case "publish":
            return await PublishAsync(container, folder, manifestBlob, current, etag);

        case "promote":
        {
            var m = Require(current);
            m.Facilities = ParseFacilities(Opt("facilities"));
            await WriteManifestAsync(manifestBlob, m, etag);
            Console.WriteLine($"{m.Version} is now for: {string.Join(", ", m.Facilities)}.");
            return 0;
        }

        case "pause":
        case "resume":
        {
            var m = Require(current);
            m.Paused = command == "pause";
            await WriteManifestAsync(manifestBlob, m, etag);
            Console.WriteLine(m.Paused
                ? $"Paused: no facility installs {m.Version} until 'resume'."
                : $"Resumed: facilities in the rollout install {m.Version}.");
            return 0;
        }

        case "rollback":
        {
            var m = Require(current);
            var prev = m.Previous ?? throw new InvalidOperationException(
                $"The manifest has no previous release to roll back to (current: {m.Version}).");
            var rolledBack = new UpdateManifest
            {
                Version = prev.Version,
                File = prev.File,
                Sha256 = prev.Sha256,
                Size = prev.Size,
                Facilities = m.Facilities,
                Rollback = true,
                Notes = $"Rolled back from {m.Version}",
                PublishedBy = Opt("by", Environment.UserName),
                PublishedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                Commit = string.Empty,
            };
            await WriteManifestAsync(manifestBlob, rolledBack, etag);
            Console.WriteLine($"Rolled back: facilities on {m.Version} go back to {prev.Version}.");
            return 0;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

BlobServiceClient Connect()
{
    if (Opt("account") is { Length: > 0 } account)
        return new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"),
            new DefaultAzureCredential(includeInteractiveCredentials: true));

    var secretsPath = Opt("secrets", Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "release.secrets.json")));
    if (!File.Exists(secretsPath))
        throw new FileNotFoundException($"Secrets file not found: {secretsPath}. Pass --secrets <file> or --account <name>.");
    using var doc = JsonDocument.Parse(File.ReadAllText(secretsPath));
    var cs = doc.RootElement.TryGetProperty("AzureBlobStorage", out var v) ? v.GetString() : null;
    if (string.IsNullOrWhiteSpace(cs) || cs.Contains("..."))
        throw new InvalidOperationException($"'AzureBlobStorage' is not filled in in {secretsPath}.");
    return new BlobServiceClient(cs);
}

async Task<int> PublishAsync(BlobContainerClient container, string folder, BlobClient manifestBlob, UpdateManifest? current, ETag? etag)
{
    var exe = Opt("exe");
    if (!File.Exists(exe))
        throw new FileNotFoundException($"--exe not found: '{exe}'.");

    var fileVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion;
    if (!Version.TryParse(fileVersion, out var version))
        throw new InvalidOperationException($"{exe} has no usable file version ('{fileVersion}').");

    if (current is not null && Version.TryParse(current.Version, out var published) && version <= published)
        throw new InvalidOperationException(
            $"{version} is not newer than the published {published}. Build again (the version is the build time).");

    var facilities = ParseFacilities(Opt("facilities"));
    var size = new FileInfo(exe).Length;
    string sha256;
    await using (var stream = File.OpenRead(exe))
        sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();

    // The exe: uploaded once, never replaced.
    var fileName = $"AHNiRSE-{version}.exe";
    var exeBlob = container.GetBlobClient($"{folder}/{fileName}");
    try
    {
        Console.WriteLine($"Uploading {container.Name}/{exeBlob.Name} ({size / 1024.0 / 1024.0:0.0} MB)...");
        await exeBlob.UploadAsync(exe, new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" },
            Metadata = new Dictionary<string, string> { ["sha256"] = sha256, ["version"] = version.ToString() },
        });
    }
    catch (RequestFailedException ex) when (ex.Status is 409 or 412)
    {
        var props = await exeBlob.GetPropertiesAsync();
        if (!props.Value.Metadata.TryGetValue("sha256", out var existing) || existing != sha256)
            throw new InvalidOperationException($"{exeBlob.Name} already exists with different content; it is never replaced.");
        Console.WriteLine($"{exeBlob.Name} is already uploaded (same SHA-256).");
    }

    // The manifest, last, so it never points to an exe that isn't there.
    var manifest = new UpdateManifest
    {
        Version = version.ToString(),
        File = fileName,
        Sha256 = sha256,
        Size = size,
        Facilities = facilities,
        Notes = Opt("notes"),
        PublishedBy = Opt("by", Environment.UserName),
        PublishedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
        Commit = Opt("commit"),
        Previous = current is null
            ? null
            : new UpdateManifestRelease { Version = current.Version, File = current.File, Sha256 = current.Sha256, Size = current.Size },
    };
    await WriteManifestAsync(manifestBlob, manifest, etag);

    Console.WriteLine($"Published {version} for: {string.Join(", ", facilities)}.");
    Console.WriteLine($"  SHA-256 {sha256}");
    Console.WriteLine("  Facilities pick it up within CheckMinutes + SpreadMinutes (about 15 min), between report runs.");
    return 0;
}

static async Task<(UpdateManifest? Manifest, ETag? ETag)> ReadManifestAsync(BlobClient blob)
{
    try
    {
        var result = await blob.DownloadContentAsync();
        var m = JsonSerializer.Deserialize<UpdateManifest>(result.Value.Content.ToString(), UpdateManifest.Json);
        return (m, result.Value.Details.ETag);
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
        return (null, null);
    }
}

static async Task WriteManifestAsync(BlobClient blob, UpdateManifest manifest, ETag? etag)
{
    var json = JsonSerializer.Serialize(manifest, UpdateManifest.Json);
    try
    {
        await blob.UploadAsync(new BinaryData(Encoding.UTF8.GetBytes(json)), new BlobUploadOptions
        {
            // Only if nobody changed it since it was read (or, for the first release, nobody created it).
            Conditions = etag is { } e ? new BlobRequestConditions { IfMatch = e } : new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json", CacheControl = "no-cache" },
        });
    }
    catch (RequestFailedException ex) when (ex.Status is 409 or 412)
    {
        throw new InvalidOperationException("manifest.json was changed by someone else just now. Check 'status' and try again.");
    }
}

static UpdateManifest Require(UpdateManifest? m) =>
    m ?? throw new InvalidOperationException("No release has been published yet.");

static List<string> ParseFacilities(string value)
{
    var list = value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    if (list.Count == 0)
        throw new InvalidOperationException("--facilities is required: 'all' or a comma-separated list of DATIM ids.");
    return list.Any(f => f.Equals("all", StringComparison.OrdinalIgnoreCase)) ? ["all"] : list;
}
