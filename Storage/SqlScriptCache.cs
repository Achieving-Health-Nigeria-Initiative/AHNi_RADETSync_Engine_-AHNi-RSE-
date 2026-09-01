using AHNiRSE.Configuration;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Storage;

public class SqlScriptCache
{
    private readonly StorageSettings _settings;
    private readonly Dictionary<string, string> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly string[] ScriptNames =
    {
        "radet_query.sql",
        "hts_query.sql",
        "index_query.sql",
        "pmtcthts_query.sql",
        "maternal_query.sql"
    };

    public SqlScriptCache(IOptions<StorageSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task<string> GetAsync(string scriptName, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(scriptName, out var cached))
                return cached;

            var blobClient = new BlobServiceClient(_settings.AzureBlobConnectionString);
            var container  = blobClient.GetBlobContainerClient(_settings.ScriptContainer);
            var blob       = container.GetBlobClient($"{_settings.ScriptPrefix}{scriptName}");

            var response = await blob.DownloadContentAsync(ct);
            var sql = response.Value.Content.ToString().TrimStart('﻿');

            _cache[scriptName] = sql;
            return sql;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<string> GetRadetSqlAsync(CancellationToken ct = default)    => GetAsync(ScriptNames[0], ct);
    public Task<string> GetHtsSqlAsync(CancellationToken ct = default)      => GetAsync(ScriptNames[1], ct);
    public Task<string> GetIndexSqlAsync(CancellationToken ct = default)    => GetAsync(ScriptNames[2], ct);
    public Task<string> GetPmtctHtsSqlAsync(CancellationToken ct = default) => GetAsync(ScriptNames[3], ct);
    public Task<string> GetMaternalSqlAsync(CancellationToken ct = default) => GetAsync(ScriptNames[4], ct);
}
