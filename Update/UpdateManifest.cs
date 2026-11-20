using System.Text.Json;
using System.Text.Json.Serialization;

namespace AHNiRSE.Update;

/// <summary>
/// &lt;Container&gt;/&lt;Folder&gt;/manifest.json: the release facilities should run. Written only by tools/RsePublish
/// (deploy\Build-Release.ps1 -Publish, deploy\Manage-Update.ps1).
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>The release's version, e.g. 2026.11.20.1500 (the exe's own file version).</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Its exe, in the same folder: AHNiRSE-&lt;version&gt;.exe.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>SHA-256 of that exe, hex.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>Facilities that should install it: DATIM ids, or "all".</summary>
    public List<string> Facilities { get; set; } = [];

    /// <summary>true = nobody installs anything until it is set back to false.</summary>
    public bool Paused { get; set; }

    /// <summary>true = facilities on a newer version go back to this one (only set by a rollback).</summary>
    public bool Rollback { get; set; }

    public string Notes { get; set; } = string.Empty;
    public string PublishedBy { get; set; } = string.Empty;
    public string PublishedAtUtc { get; set; } = string.Empty;
    public string Commit { get; set; } = string.Empty;

    /// <summary>The release this one replaced, kept so it can be rolled back to.</summary>
    public UpdateManifestRelease? Previous { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Readable when opened in the portal: "+00:00", not "+00:00".
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public bool IsFor(string datimId) =>
        Facilities.Any(f => f.Equals("all", StringComparison.OrdinalIgnoreCase)
                            || (datimId.Length > 0 && f.Trim().Equals(datimId, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>An earlier release, as kept in <see cref="UpdateManifest.Previous"/>.</summary>
public sealed class UpdateManifestRelease
{
    public string Version { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}
