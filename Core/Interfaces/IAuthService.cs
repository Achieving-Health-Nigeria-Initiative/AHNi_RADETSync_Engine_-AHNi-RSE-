namespace AHNiRSE.Core.Interfaces;

/// <summary>
/// Validates the installation license before any report job runs.
/// Called once at startup by ReportOrchestrator.
/// Throws AuthException on failure so the host process logs and exits cleanly.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Returns true when the license key is valid against the remote auth endpoint.
    /// Returns false when validation fails gracefully (expired, wrong key).
    /// Throws on network errors so the caller can decide whether to retry.
    /// </summary>
    Task<bool> ValidateInstallationAsync(CancellationToken ct = default);
}
