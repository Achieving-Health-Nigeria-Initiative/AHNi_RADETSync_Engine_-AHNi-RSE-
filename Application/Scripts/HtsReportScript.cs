using AHNiRSE.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AHNiRSE.Application.Scripts;

/// <summary>
/// HTS report stub â€” kept for DI registration and config compatibility.
///
/// HTS data is now written as Sheet 2 of the RADET workbook inside
/// RadetReportScript. This script should remain Enabled=false in
/// appsettings.json. If it is ever accidentally enabled, it logs a
/// clear warning and returns immediately so no duplicate file is produced.
/// </summary>
public class HtsReportScript(ILogger<HtsReportScript> logger) : IReportScript
{
    public string Name => "HTS";

    public Task<(int RowCount, string UploadUrl)> RunAsync(
        CycleContext context,
        CancellationToken ct = default)
    {
        logger.LogWarning(
            "[HTS] HtsReportScript is registered but HTS data is now exported as " +
            "Sheet 2 of the RADET workbook by RadetReportScript. " +
            "Set Enabled=false for HTS in appsettings.json to suppress this warning.");

        return Task.FromResult((0, string.Empty));
    }
}

