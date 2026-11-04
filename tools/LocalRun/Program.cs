using AHNiRSE.Configuration;
using AHNiRSE.Platform;
using AHNiRSE.Reports;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Runs one RADET workbook cycle on this PC: SQL downloaded from blob storage as the service does,
// data from a local LAMISPlus database, and the workbook saved to a local folder instead of uploaded.
//
//   dotnet run --project tools/LocalRun -- <appsettings.json> <secrets.json> <database> <output folder> [as-of date]
//
//   appsettings.json  the service's settings (Reports, Storage, ...)
//   secrets.json      { "ConnectionStrings": { "AzureBlobStorage": "..." } } - keep it out of git
//   database          local database name (user postgres on localhost:5432; set its password in the
//                     LOCALRUN_DB_PASSWORD environment variable first)
//   as-of date        yyyy-MM-dd: run as a force backfill for that date instead of today
//
// Absolute paths are safest: configuration files resolve against the tool's build folder.
if (args.Length < 4)
{
    Console.Error.WriteLine("usage: <appsettings.json> <secrets.json> <database> <output folder> [as-of date]");
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
var azure = new AzureStorageProvider(
    new BlobServiceClient(cfg.GetConnectionString("AzureBlobStorage")),
    Options.Create(storage), lf.CreateLogger<AzureStorageProvider>());
var script = new RadetReportScript(
    new LocalOutputStorage(azure, args[3], lf.CreateLogger("LocalOutput")),
    new PostgresService(Options.Create(db), lf.CreateLogger<PostgresService>()),
    new ExcelExporter(lf.CreateLogger<ExcelExporter>()),
    Options.Create(reports),
    lf.CreateLogger<RadetReportScript>());

var backfill = args.Length > 4;
var asOf = backfill ? DateOnly.Parse(args[4]) : DateOnly.FromDateTime(DateTime.Now);
var context = new CycleContext(asOf, asOf, "local-" + DateTime.Now.ToString("HHmmss"),
    backfill ? "ForceBackfill:local" : "LocalRun", Backfill: backfill);

var sw = System.Diagnostics.Stopwatch.StartNew();
var (rows, path, _) = await script.RunAsync(context);
Console.WriteLine($"DONE {rows:N0} RADET rows in {sw.Elapsed.TotalMinutes:F1} min -> {path}");
return 0;

/// Downloads scripts from blob storage as the service does, but "uploads" by copying to a local folder.
sealed class LocalOutputStorage(IStorageProvider blob, string outputRoot, ILogger log) : IStorageProvider
{
    public Task<string> DownloadAsync(string sourcePath, CancellationToken ct = default)
        => blob.DownloadAsync(sourcePath, ct);

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
