using AHNiRSE;
using AHNiRSE.Application.Scripts;
using AHNiRSE.Application.Services;
using AHNiRSE.Configuration;
using AHNiRSE.Core.Clock;
using AHNiRSE.Core.Interfaces;
using AHNiRSE.Infrastructure.Auth;
using AHNiRSE.Infrastructure.Audit;
using AHNiRSE.Infrastructure.Clock;
using AHNiRSE.Infrastructure.Database;
using AHNiRSE.Infrastructure.Export;
using AHNiRSE.Infrastructure.Health;
using AHNiRSE.Infrastructure.Heartbeat;
using AHNiRSE.Infrastructure.Storage;
using AHNiRSE.Shared;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var isWindowsService = WindowsServiceHelpers.IsWindowsService();

var logDir = isWindowsService
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AHNi-RSE", "Logs")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AHNi-RSE", "Logs");

Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("MachineName", Environment.MachineName)
    .Enrich.WithProperty("IsWindowsService", isWindowsService)
    .WriteTo.Console(
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{@Exception}")
    .WriteTo.File(
        path: Path.Combine(logDir, "ahni-rse-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] [{MachineName}] {Message:lj}{NewLine}{@Exception}")
    .CreateLogger();

try
{
    Log.Information("=================================================");
    Log.Information("[Startup] AHNi RADETSync Engine (AHNi-RSE) starting (WAT timezone enforcement active)");
    Log.Information("[Startup] Log directory: {LogDir}", logDir);
    Log.Information("[Startup] Running as Windows Service: {IsService}", isWindowsService);
    Log.Information("=================================================");

    var builder = Host.CreateApplicationBuilder(args);

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(Log.Logger);

    if (OperatingSystem.IsWindows())
        builder.Services.AddWindowsService(options => options.ServiceName = "AHNi-RSE");

    builder.Services.Configure<HostOptions>(o =>
    {
        o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
    });

    // â”€â”€ CONFIGURATION â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    builder.Services.Configure<StorageSettings>(builder.Configuration.GetSection("Storage"));
    builder.Services.Configure<DatabaseSettings>(builder.Configuration.GetSection("Database"));
    builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection("Auth"));
    builder.Services.Configure<ReportSettings>(builder.Configuration.GetSection("Reports"));
    builder.Services.Configure<HeartbeatSettings>(builder.Configuration.GetSection("Heartbeat"));

    builder.Services.PostConfigure<StorageSettings>(s =>
    {
        var raw = builder.Configuration["StorageProvider"];
        if (!string.IsNullOrWhiteSpace(raw)) s.Provider = raw;
    });

    // â”€â”€ CLOCK â€” single source of truth for WAT date â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // WatClock.TodayInWat() is called ONCE per cycle in ReportOrchestrator.
    // The result is passed as CycleContext.RunDate to every script.
    // No script or service may call DateTime.Today or DateTime.Now directly.
    builder.Services.AddSingleton<IReportClock, WatClock>();

    // â”€â”€ AZURE STORAGE â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    builder.Services.AddSingleton<BlobServiceClient>(sp =>
    {
        var cs = builder.Configuration.GetConnectionString("AzureBlobStorage")
            ?? throw new InvalidOperationException("Missing 'AzureBlobStorage' connection string.");
        return new BlobServiceClient(cs);
    });

    builder.Services.AddSingleton<TableServiceClient>(sp =>
    {
        var cs = builder.Configuration.GetConnectionString("AzureBlobStorage")
            ?? throw new InvalidOperationException("Missing 'AzureBlobStorage' connection string.");
        return new TableServiceClient(cs);
    });

    builder.Services.AddSingleton<BlobConnectionCheck>();

    // â”€â”€ SHARED STATE & HEARTBEAT â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    builder.Services.AddSingleton<FacilityRunState>();
    builder.Services.AddSingleton<AzureHeartbeatService>();
    builder.Services.AddSingleton<IHeartbeatService>(sp => sp.GetRequiredService<AzureHeartbeatService>());

    // â”€â”€ STORAGE PROVIDER â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    builder.Services.AddSingleton<LocalStorageProvider>();
    builder.Services.AddSingleton<AzureStorageProvider>();
    builder.Services.AddSingleton<IStorageProvider>(sp =>
        StorageProviderFactory.Create(sp)
        ?? throw new InvalidOperationException("StorageProvider not configured."));

    // â”€â”€ INFRASTRUCTURE â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    builder.Services.AddSingleton<IDatabaseService, PostgresService>();
    builder.Services.AddSingleton<IExcelExporter, ExcelExporter>();
    builder.Services.AddHttpClient();
    builder.Services.AddHttpClient(nameof(InstallationAuthService), client =>
    {
        // Match Postman's request profile so Cloudflare bot protection does not challenge the request.
        // Postman uses HTTP/1.1 and these exact headers; dotnet-httpclient default triggers a JS challenge.
        client.DefaultRequestVersion = System.Net.HttpVersion.Version11;
        client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PostmanRuntime/7.43.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        // Let the handler own Accept-Encoding so responses are automatically decompressed.
        // Setting it manually (as a header) compresses the body but skips decompression.
        AutomaticDecompression = System.Net.DecompressionMethods.GZip
                               | System.Net.DecompressionMethods.Deflate
                               | System.Net.DecompressionMethods.Brotli
    });
    builder.Services.AddSingleton<IAuthService, InstallationAuthService>();
    builder.Services.AddSingleton<IReportCycleAudit, ReportCycleAudit>();

    // â”€â”€ REPORT SCRIPTS â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // To add a new report: implement IReportScript, register it here.
    builder.Services.AddSingleton<IReportScript, RadetReportScript>();
    builder.Services.AddSingleton<IReportScript, HtsReportScript>();

    builder.Services.AddSingleton<ScriptManager>();
    builder.Services.AddSingleton<ReportOrchestrator>();

    // â”€â”€ WORKER â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();

    // â”€â”€ STARTUP HEALTH CHECK â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    using var scope = host.Services.CreateScope();
    var storageProvider = scope.ServiceProvider
        .GetRequiredService<IOptions<StorageSettings>>().Value.Provider;
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Log WAT time so we can confirm timezone is correct at startup
    var clock = scope.ServiceProvider.GetRequiredService<IReportClock>();
    startupLogger.LogInformation(
        "[Startup] Current WAT time: {WatNow} | Today in WAT: {WatDate}",
        clock.NowInWat().ToString("yyyy-MM-dd HH:mm:ss zzz"),
        clock.TodayInWat().ToString("yyyy-MM-dd"));

    if (storageProvider.Equals("Azure", StringComparison.OrdinalIgnoreCase))
    {
        var check = scope.ServiceProvider.GetRequiredService<BlobConnectionCheck>();
        bool isHealthy = await check.RunAsync();
        if (!isHealthy)
        {
            startupLogger.LogWarning(
                "[Startup] Azure Blob health check FAILED. Continuing service start; runtime retries will handle transient network/storage outages.");
        }
        else
        {
            startupLogger.LogInformation("[Startup] Azure Blob health check PASSED.");
        }
    }

    startupLogger.LogInformation("[Startup] Starting host...");
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "[Startup] AHNi RADETSync Engine (AHNi-RSE) terminated unexpectedly: {Message}", ex.Message);
    return 1;
}
finally
{
    Log.Information("[Shutdown] AHNi RADETSync Engine (AHNi-RSE) shut down. Logs: {LogDir}", logDir);
    await Log.CloseAndFlushAsync();
}

