using AHNiRSE.Configuration;
using AHNiRSE.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Infrastructure.Storage;

/// <summary>
/// Reads StorageSettings.Provider and returns the matching implementation.
/// "Azure" â†’ AzureStorageProvider
/// "Local" â†’ LocalStorageProvider  (default for dev)
/// </summary>
public static class StorageProviderFactory
{
    public static IStorageProvider? Create(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<StorageSettings>>().Value;

        return settings.Provider.ToUpperInvariant() switch
        {
            "AZURE" => sp.GetRequiredService<AzureStorageProvider>(),
            "LOCAL" => sp.GetRequiredService<LocalStorageProvider>(),
            _ => null
        };
    }
}
