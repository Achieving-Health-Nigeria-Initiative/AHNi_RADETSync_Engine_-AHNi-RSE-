using AHNiRSE.Configuration;
using AHNiRSE.Platform;
using AHNiRSE.Reports;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Runs one RADET workbook cycle on this PC: SQL downloaded from blob storage as the service does,
// data from a local LAMISPlus database, and the workbook saved to a local folder instead of uploaded,
// under a folder named after the container it would go to (autoreport or community-center).
//
//   dotnet run --project tools/LocalRun -- <appsettings.json> <secrets.json> <database> <output folder> [as-of date] [raw]
//
//   appsettings.json  the service's settings (Reports, Storage, ...)
//   secrets.json      { "ConnectionStrings": { "AzureBlobStorage": "..." } } - keep it out of git
//   database          local database name (user postgres on localhost:5432; set its password in the
//                     LOCALRUN_DB_PASSWORD environment variable first)
//   as-of date        yyyy-MM-dd: run as a force backfill for that date instead of today
//   raw               with an as-of date: the raw report (Parquet files in raw-report\<date>\) instead of the workbook
//
// Set LOCALRUN_SCRIPT_DIR to a folder of .sql files to run those instead of the blob's scripts.
//
// Absolute paths are safest: configuration files resolve against the tool's build folder.
if (args.Length < 4)
{
    Console.Error.WriteLine("usage: <appsettings.json> <secrets.json> <database> <output folder> [as-of date] [raw]");
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
// The local Postgres password comes from the LOCALRUN_DB_PASSWORD environment variable - never in code.
var dbPassword = Environment.GetEnvironmentVariable("LOCALRUN_DB_PASSWORD")
    ?? throw new InvalidOperationException("Set LOCALRUN_DB_PASSWORD to the local postgres password.");
db.ConnectionString = $"Host=localhost;Port=5432;Database={args[2]};Username=postgres;Password={dbPassword};";

using var lf = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
var database = new PostgresService(Options.Create(db), lf.CreateLogger<PostgresService>());
// Community centre or gen-pop, from appsettings.json (Facility:IsCommunityCentre), as in the service.
var facilitySettings = cfg.GetSection("Facility").Get<FacilitySettings>() ?? new FacilitySettings();
var profile = new FacilityProfile(Options.Create(reports), Options.Create(storage), Options.Create(facilitySettings));
profile.Log(lf.CreateLogger("Facility"));

var azure = new AzureStorageProvider(
    new BlobServiceClient(cfg.GetConnectionString("AzureBlobStorage")),
    Options.Create(storage), lf.CreateLogger<AzureStorageProvider>(), profile);
var output = new LocalOutputStorage(azure, Path.Combine(args[3], profile.UploadContainer), lf.CreateLogger("LocalOutput"));
var workbook = new RadetWorkbook(
    new ReportScripts(output, Options.Create(reports)),
    output,
    database,
    new ExcelExporter(lf.CreateLogger<ExcelExporter>()),
    profile,
    lf.CreateLogger<RadetWorkbook>());

var backfill = args.Length > 4;
var asOf = backfill ? DateOnly.Parse(args[4]) : DateOnly.FromDateTime(DateTime.Now);
var context = new CycleContext(asOf, asOf, "local-" + DateTime.Now.ToString("HHmmss"),
    backfill ? "ForceBackfill:local" : "LocalRun", Backfill: backfill);

var sw = System.Diagnostics.Stopwatch.StartNew();
if (backfill && args.Length > 5 && args[5].Equals("raw", StringComparison.OrdinalIgnoreCase))
{
    var raw = new RawReport(new ReportScripts(output, Options.Create(reports)), output, database, profile,
        lf.CreateLogger<RawReport>());
    var result = await raw.RunAsync(context);
    Console.WriteLine($"DONE raw report, {result.RadetRows:N0} RADET rows in {sw.Elapsed.TotalMinutes:F1} min -> {result.ManifestUrl}");
    foreach (var file in result.Files)
        Console.WriteLine($"  {file}");
    return 0;
}
var (rows, path, _) = await workbook.RunAsync(context);
Console.WriteLine($"DONE {rows:N0} RADET rows in {sw.Elapsed.TotalMinutes:F1} min -> {path}");
return 0;

/// Downloads scripts from blob storage as the service does, but "uploads" by copying to a local folder
/// (<output folder>\<container>\RADET\<date>\...).
sealed class LocalOutputStorage(IStorageProvider blob, string outputRoot, ILogger log) : IStorageProvider
{
    // LOCALRUN_SCRIPT_DIR: a folder of .sql files to use instead of the blob's report-script/ (to try a
    // script before uploading it); a script missing from the folder still comes from the blob.
    public Task<string> DownloadAsync(string sourcePath, CancellationToken ct = default)
    {
        var dir = Environment.GetEnvironmentVariable("LOCALRUN_SCRIPT_DIR");
        var local = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, Path.GetFileName(sourcePath));
        if (local is not null && File.Exists(local))
        {
            log.LogInformation("Script from LOCALRUN_SCRIPT_DIR (not the blob): {Path}", local);
            return Task.FromResult(local);
        }
        return blob.DownloadAsync(sourcePath, ct);
    }

    public Task<(string Url, DateTimeOffset UploadedAt)> UploadAsync(
        string localFilePath, string destinationPath, CancellationToken ct = default)
    {
        var dest = Path.Combine(outputRoot, destinationPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(localFilePath, dest, overwrite: true);
        log.LogInformation("Saved locally (not uploaded): {Path}", dest);
        return Task.FromResult((dest, DateTimeOffset.UtcNow));
    }
}
