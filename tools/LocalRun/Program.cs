using AHNiRSE.Backfill;
using AHNiRSE.Configuration;
using AHNiRSE.Platform;
using AHNiRSE.Reports;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Runs the RSE's report code on this PC against a local LAMISPlus database: SQL downloaded from blob storage as
// the service does, and results saved to a local folder instead of uploaded, under a folder named after the
// container they would go to (autoreport, community-center or custom-reports).
//
//   dotnet run --project tools/LocalRun -- <appsettings.json> <secrets.json> <database> <output folder> <mode...>
//
//   (no mode)                       the RADET workbook as of today
//   <as-of date> [raw]              a force backfill for that date: the workbook, or with raw the raw report
//   custom <file.sql> <start> <end> [name=value ...]
//                                   a custom script, read-only, as a "custom" job would run it
//   jobs <jobs table> <heartbeat table>
//                                   acts as this facility's RSE for force backfills in TEST tables: writes a
//                                   heartbeat row (so the Backfill app lists the facility), then claims and runs
//                                   its pending jobs once. Workbooks and raw reports are saved locally; custom
//                                   results are uploaded to LOCALRUN_CUSTOM_CONTAINER (default
//                                   custom-reports-test) so the Backfill app can download and combine them.
//                                   Live table and container names are refused.
//
//   appsettings.json  the service's settings (Reports, Storage, ...)
//   secrets.json      { "ConnectionStrings": { "AzureBlobStorage": "..." } } - keep it out of git
//   database          local database name (user postgres on localhost:5432; its password from the
//                     LOCALRUN_DB_PASSWORD environment variable, or %APPDATA%\postgresql\pgpass.conf)
//
// Set LOCALRUN_SCRIPT_DIR to a folder of .sql files to run those instead of the blob's report scripts.
// Set LOCALRUN_UPLOAD=1 to upload the workbook for real, to <container>/<Reports:WorkbookFolder>/<date>/ (TEST-RADET;
// refused when the folder is the live RADET). Raw reports are still saved locally.
// Absolute paths are safest: configuration files resolve against the tool's build folder.
if (args.Length < 4)
{
    Console.Error.WriteLine("usage: <appsettings.json> <secrets.json> <database> <output folder> [as-of date [raw] | custom ... | jobs ...]");
    return 1;
}

var cfg = new ConfigurationBuilder()
    .AddJsonFile(Path.GetFullPath(args[0]))
    .AddJsonFile(Path.GetFullPath(args[1]))
    .Build();
var storage = cfg.GetSection("Storage").Get<StorageSettings>()!;
storage.Azure.TempDownloadPath = Path.Combine(args[3], "_scripts");
var reports = cfg.GetSection("Reports").Get<ReportSettings>()!;
var db = cfg.GetSection("Database").Get<DatabaseSettings>()!;
var customSettings = cfg.GetSection("CustomScript").Get<CustomScriptSettings>() ?? new CustomScriptSettings();
// The local Postgres password comes from the LOCALRUN_DB_PASSWORD environment variable or, without it, from
// PostgreSQL's own password file (%APPDATA%\postgresql\pgpass.conf) - never from code.
var dbPassword = Environment.GetEnvironmentVariable("LOCALRUN_DB_PASSWORD");
db.ConnectionString = $"Host=localhost;Port=5432;Database={args[2]};Username=postgres;"
                      + (string.IsNullOrEmpty(dbPassword) ? string.Empty : $"Password={dbPassword};");

using var lf = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
var database = new PostgresService(Options.Create(db), lf.CreateLogger<PostgresService>());
// Community centre or gen-pop, from appsettings.json (Facility:IsCommunityCentre), as in the service.
var facilitySettings = cfg.GetSection("Facility").Get<FacilitySettings>() ?? new FacilitySettings();
var profile = new FacilityProfile(Options.Create(reports), Options.Create(storage), Options.Create(facilitySettings));
profile.Log(lf.CreateLogger("Facility"));

var mode = args.Length > 4 ? args[4].ToLowerInvariant() : string.Empty;
var jobsMode = mode == "jobs";
if (jobsMode)
    customSettings.Container = Environment.GetEnvironmentVariable("LOCALRUN_CUSTOM_CONTAINER") is { Length: > 0 } c ? c : "custom-reports-test";

var blobService = new BlobServiceClient(cfg.GetConnectionString("AzureBlobStorage"));
var azure = new AzureStorageProvider(blobService, Options.Create(storage), lf.CreateLogger<AzureStorageProvider>(), profile);
var outputRoot = Path.GetFullPath(args[3]);
// LOCALRUN_UPLOAD=1: the workbook really goes up, to Reports:WorkbookFolder (TEST-RADET) - never to the live RADET folder.
var uploadWorkbook = Environment.GetEnvironmentVariable("LOCALRUN_UPLOAD") == "1";
if (uploadWorkbook && profile.WorkbookFolder.Equals("RADET", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("LOCALRUN_UPLOAD=1 refuses Reports:WorkbookFolder=RADET (the live folder).");
    return 1;
}
var output = new LocalOutputStorage(azure, outputRoot, profile.UploadContainer,
    (container, blob) => (jobsMode && container == customSettings.Container)
                         || (uploadWorkbook && container == profile.UploadContainer && blob.StartsWith(profile.WorkbookFolder + "/")),
    lf.CreateLogger("LocalOutput"));
var scripts = new ReportScripts(output, Options.Create(reports));
var workbook = new RadetWorkbook(scripts, output, database, new ExcelExporter(lf.CreateLogger<ExcelExporter>()), profile,
    lf.CreateLogger<RadetWorkbook>());
var raw = new RawReport(scripts, output, database, profile, lf.CreateLogger<RawReport>());
var custom = new CustomQuery(output, database, scripts, Options.Create(customSettings), lf.CreateLogger<CustomQuery>());
var sw = System.Diagnostics.Stopwatch.StartNew();

switch (mode)
{
    case "custom":
    {
        if (args.Length < 8)
        {
            Console.Error.WriteLine("usage: ... custom <file.sql> <start yyyy-MM-dd> <end yyyy-MM-dd> [name=value ...]");
            return 1;
        }

        // The file stands in for the blob the Backfill app would store; its SHA-256 is checked the same way.
        var sqlFile = Path.GetFullPath(args[5]);
        var bytes = await File.ReadAllBytesAsync(sqlFile);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var parameters = args.Skip(8)
            .Select(a => a.Split('=', 2))
            .ToDictionary(p => p[0].TrimStart(':').ToLowerInvariant(), p => p.Length > 1 ? p[1] : string.Empty);
        var job = new CustomJob(Path.GetFileNameWithoutExtension(sqlFile), $"{CustomJob.BlobFolder}/local/{sha}.sql", sha,
            DateOnly.Parse(args[6]), DateOnly.Parse(args[7]), parameters, "local-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        output.LocalScripts[job.ScriptBlob] = sqlFile;

        var result = await custom.RunAsync(job, Context(job.EndDate, "ForceBackfill:local-custom", backfill: true));
        Console.WriteLine($"DONE custom script, {result.Rows:N0} rows in {sw.Elapsed.TotalSeconds:F1} s -> {result.Url}");
        return 0;
    }

    case "jobs":
        return await RunJobsAsync();

    case "":
    {
        var (rows, path, _) = await workbook.RunAsync(Context(DateOnly.FromDateTime(DateTime.Now), "LocalRun", backfill: false));
        Console.WriteLine($"DONE {rows:N0} RADET rows in {sw.Elapsed.TotalMinutes:F1} min -> {path}");
        return 0;
    }

    default:
    {
        var asOf = DateOnly.Parse(args[4]);
        var context = Context(asOf, "ForceBackfill:local", backfill: true);
        if (args.Length > 5 && args[5].Equals("raw", StringComparison.OrdinalIgnoreCase))
        {
            var result = await raw.RunAsync(context);
            Console.WriteLine($"DONE raw report, {result.RadetRows:N0} RADET rows in {sw.Elapsed.TotalMinutes:F1} min -> {result.ManifestUrl}");
            foreach (var file in result.Files)
                Console.WriteLine($"  {file}");
            return 0;
        }
        var (rows, path, _) = await workbook.RunAsync(context);
        Console.WriteLine($"DONE {rows:N0} RADET rows in {sw.Elapsed.TotalMinutes:F1} min -> {path}");
        return 0;
    }
}

CycleContext Context(DateOnly asOf, string trigger, bool backfill) =>
    new(asOf, asOf, "local-" + DateTime.Now.ToString("HHmmss"), trigger, Backfill: backfill);

async Task<int> RunJobsAsync()
{
    if (args.Length < 7)
    {
        Console.Error.WriteLine("usage: ... jobs <jobs table> <heartbeat table>");
        return 1;
    }
    var jobsTable = args[5];
    var heartbeatTable = args[6];
    // Never the live tables or container: this PC is not a facility.
    if (jobsTable.Equals("ForceBackfillJobs", StringComparison.OrdinalIgnoreCase)
        || heartbeatTable.Equals("FacilityHeartbeat", StringComparison.OrdinalIgnoreCase)
        || customSettings.Container.Equals("custom-reports", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Refusing the live ForceBackfillJobs / FacilityHeartbeat tables or the custom-reports container.");
        return 1;
    }

    var facility = database.GetFacilityInfo();
    if (facility.IsEmpty)
        throw new InvalidOperationException("The facility could not be read from the database.");
    var tables = new TableServiceClient(cfg.GetConnectionString("AzureBlobStorage"));
    var version = typeof(CustomQuery).Assembly.GetName().Version?.ToString() ?? "local";

    // The heartbeat row the Backfill app reads its facility list from (BayCentral's columns).
    var heartbeat = tables.GetTableClient(heartbeatTable);
    await heartbeat.CreateIfNotExistsAsync();
    await heartbeat.UpsertEntityAsync(new TableEntity(facility.DatimId, "latest")
    {
        ["FacilityName"] = facility.FacilityName,
        ["DatimId"] = facility.DatimId,
        ["State"] = facility.State,
        ["Status"] = "online",
        ["LastSeen"] = DateTimeOffset.UtcNow,
        ["OfflineThresholdMinutes"] = 60,
        ["MachineName"] = Environment.MachineName,
        ["AppVersion"] = Environment.GetEnvironmentVariable("LOCALRUN_APP_VERSION") is { Length: > 0 } v ? v : version,
        ["UpdateStatus"] = Environment.GetEnvironmentVariable("LOCALRUN_UPDATE_STATUS") ?? "UpToDate",
        ["UpdateTargetVersion"] = Environment.GetEnvironmentVariable("LOCALRUN_UPDATE_TARGET") ?? string.Empty,
        ["UpdateError"] = Environment.GetEnvironmentVariable("LOCALRUN_UPDATE_ERROR") ?? string.Empty,
        ["UpdateLastChecked"] = DateTimeOffset.UtcNow.ToString("o"),
        ["UpdateLastInstalled"] = string.Empty,
    }, TableUpdateMode.Merge);
    Console.WriteLine($"Heartbeat written to {heartbeatTable}: {facility.FacilityName} ({facility.DatimId}).");

    var store = new ForceBackfillJobStore(tables,
        Options.Create(new ForceBackfillSettings { TableName = jobsTable, MaxAttempts = 3 }),
        lf.CreateLogger<ForceBackfillJobStore>(), Options.Create(storage), profile);
    await store.EnsureTableExistsAsync();

    var ran = 0;
    while (await store.TryClaimNextAsync(facility.DatimId) is { } job)
    {
        ran++;
        Console.WriteLine($"-- job {job.RowKey} ({job.ReportType}, as of {job.AsOfDate:yyyy-MM-dd})");
        try
        {
            var context = Context(job.AsOfDate, $"ForceBackfill:{job.RowKey}", backfill: true);
            BackfillResult result;
            switch (job.ReportType)
            {
                case BackfillReportType.Custom:
                    result = await custom.RunAsync(job.Custom!, context);
                    break;
                case BackfillReportType.Raw:
                    var r = await raw.RunAsync(context);
                    result = new BackfillResult(r.RadetRows, r.ManifestUrl, r.UploadedAt, r.Files);
                    break;
                default:
                    var (rows, url, at) = await workbook.RunAsync(context);
                    result = new BackfillResult(rows, url, at, []);
                    break;
            }
            await store.CompleteAsync(job, result);
            Console.WriteLine($"   uploaded: {result.Rows:N0} rows -> {result.Url}");
        }
        catch (Exception ex)
        {
            await store.FailAsync(job, ex.Message);
            Console.WriteLine($"   FAILED: {ex.Message}");
        }
    }
    Console.WriteLine($"DONE {ran} job(s) for {facility.DatimId} in {sw.Elapsed.TotalSeconds:F0} s.");
    return 0;
}

/// Downloads scripts from blob storage as the service does, but "uploads" by copying to a local folder
/// (<output folder>\<container>\...). toAzure picks what really goes up: custom results in jobs mode, and the
/// workbook (to Reports:WorkbookFolder) with LOCALRUN_UPLOAD=1.
sealed class LocalOutputStorage(IStorageProvider blob, string root, string container, Func<string, string, bool> toAzure, ILogger log)
    : IStorageProvider
{
    /// <summary>Blob paths answered from a local file instead (custom mode's script).</summary>
    public Dictionary<string, string> LocalScripts { get; } = new(StringComparer.OrdinalIgnoreCase);

    // LOCALRUN_SCRIPT_DIR: a folder of .sql files to use instead of the blob's report-script/ (to try a
    // script before uploading it); a script missing from the folder still comes from the blob.
    public Task<string> DownloadAsync(string sourcePath, CancellationToken ct = default)
    {
        if (LocalScripts.TryGetValue(sourcePath, out var file))
        {
            // A copy, since the caller deletes what it is given.
            var copy = Path.Combine(Path.GetTempPath(), "localrun-" + Guid.NewGuid().ToString("N") + ".sql");
            File.Copy(file, copy);
            return Task.FromResult(copy);
        }

        var dir = Environment.GetEnvironmentVariable("LOCALRUN_SCRIPT_DIR");
        var local = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, Path.GetFileName(sourcePath));
        if (local is not null && File.Exists(local) && !sourcePath.StartsWith(CustomJob.BlobFolder + "/"))
        {
            log.LogInformation("Script from LOCALRUN_SCRIPT_DIR (not the blob): {Path}", local);
            return Task.FromResult(local);
        }
        return blob.DownloadAsync(sourcePath, ct);
    }

    public Task<(string Url, DateTimeOffset UploadedAt)> UploadAsync(
        string localFilePath, string destinationPath, CancellationToken ct = default)
        => Task.FromResult(SaveLocally(localFilePath, container, destinationPath));

    public Task<(string Url, DateTimeOffset UploadedAt)> UploadToAsync(
        string toContainer, string localFilePath, string blobName, CancellationToken ct = default)
        => toAzure(toContainer, blobName)
            ? blob.UploadToAsync(toContainer, localFilePath, blobName, ct)
            : Task.FromResult(SaveLocally(localFilePath, toContainer, blobName));

    private (string, DateTimeOffset) SaveLocally(string localFilePath, string toContainer, string path)
    {
        var dest = Path.Combine(root, toContainer, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(localFilePath, dest, overwrite: true);
        log.LogInformation("Saved locally (not uploaded): {Path}", dest);
        return (dest, DateTimeOffset.UtcNow);
    }
}
