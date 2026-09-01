using AHNiRSE;
using AHNiRSE.Configuration;
using AHNiRSE.Core;
using AHNiRSE.Data;
using AHNiRSE.Export;
using AHNiRSE.Infrastructure;
using AHNiRSE.Storage;
using Prometheus;

// --run-now [yyyy-MM-dd]
// Runs a single combined cycle immediately and exits. Used for manual testing.
// date is optional — omit to use today.
bool runNow = args.Contains("--run-now");
bool uploadScripts = args.Contains("--upload-scripts");
DateOnly runDate = DateOnly.FromDateTime(DateTime.Today);

if (runNow)
{
    var idx = Array.IndexOf(args, "--run-now");
    if (idx + 1 < args.Length && !args[idx + 1].StartsWith("--")
        && DateOnly.TryParse(args[idx + 1], out var d))
        runDate = d;
}

var builder = Host.CreateApplicationBuilder(args);

// Configuration sections
builder.Services.Configure<DatabaseSettings>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<StorageSettings>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<ReportSettings>(builder.Configuration.GetSection("Reports"));

// Infrastructure
builder.Services.AddSingleton<DatabaseConnectionSelector>();

// Data
builder.Services.AddSingleton<ParameterisedSqlRunner>();

// Storage
builder.Services.AddSingleton<SqlScriptCache>();
builder.Services.AddSingleton<BlobUploader>();

// Export
builder.Services.AddSingleton<FiveSheetExcelExporter>();

// Core
builder.Services.AddSingleton<CombinedReportJob>();
builder.Services.AddSingleton<CycleOrchestrator>();

if (!runNow && !uploadScripts)
    builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// Expose Prometheus metrics on /metrics
var metricsPort = builder.Configuration.GetValue<int>("Metrics:Port", 9200);
var metricServer = new MetricServer(port: metricsPort);
try { metricServer.Start(); }
catch (Exception ex) { Console.Error.WriteLine($"[WARN] Metrics server not started: {ex.Message}"); }

if (uploadScripts)
{
    var storageSettings = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageSettings>>().Value;
    var serviceClient = new Azure.Storage.Blobs.BlobServiceClient(storageSettings.AzureBlobConnectionString);
    var container = serviceClient.GetBlobContainerClient(storageSettings.ScriptContainer);
    var scriptDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Database", "SqlScripts");

    string[] sqlFiles = ["radet_query.sql", "hts_query.sql", "index_query.sql", "pmtcthts_query.sql", "maternal_query.sql"];
    foreach (var file in sqlFiles)
    {
        var localPath = Path.GetFullPath(Path.Combine(scriptDir, file));
        var blobName = $"{storageSettings.ScriptPrefix}{file}";
        Console.WriteLine($"Uploading {file} → {storageSettings.ScriptContainer}/{blobName}");
        await using var stream = File.OpenRead(localPath);
        await container.GetBlobClient(blobName).UploadAsync(stream, overwrite: true);
        Console.WriteLine($"  OK");
    }
    Console.WriteLine("All SQL scripts uploaded.");
    metricServer.Stop();
    return 0;
}

if (runNow)
{
    var orchestrator = host.Services.GetRequiredService<CycleOrchestrator>();
    await orchestrator.RunCycleAsync(runDate, CancellationToken.None);
    metricServer.Stop();
    return 0;
}

host.Run();
return 0;