using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ProxySiu.Api.Models;
using ProxySiu.Api.Options;

namespace ProxySiu.Api.Storage;

public sealed class JsonProxyStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProxyPoolOptions _options;
    private readonly ILogger<JsonProxyStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _filePath;
    private readonly string _backupPath;
    private ProxyPoolState _state = new();
    private bool _initialized;

    public JsonProxyStore(
        IOptions<ProxyPoolOptions> options,
        IHostEnvironment environment,
        ILogger<JsonProxyStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        _filePath = Path.IsPathRooted(_options.DataFile)
            ? _options.DataFile
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, _options.DataFile));
        _backupPath = $"{_filePath}.bak";
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var loadedExistingState = false;
            var recoveredFromBackup = false;
            var primaryWasCorrupt = false;
            if (File.Exists(_filePath))
            {
                try
                {
                    _state = await LoadStateAsync(_filePath, cancellationToken);
                    loadedExistingState = true;
                }
                catch (Exception exception) when (exception is JsonException or IOException)
                {
                    primaryWasCorrupt = true;
                    var corruptPath = $"{_filePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                    File.Copy(_filePath, corruptPath, true);
                    _logger.LogError(exception, "Proxy pool data is corrupt; copied it to {CorruptPath}", corruptPath);

                    if (File.Exists(_backupPath))
                    {
                        try
                        {
                            _state = await LoadStateAsync(_backupPath, cancellationToken);
                            loadedExistingState = true;
                            recoveredFromBackup = true;
                            _logger.LogWarning("Recovered proxy pool data from {BackupPath}", _backupPath);
                        }
                        catch (Exception backupException) when (backupException is JsonException or IOException)
                        {
                            _logger.LogError(backupException, "Proxy pool backup is also unreadable: {BackupPath}", _backupPath);
                            _state = new ProxyPoolState();
                        }
                    }
                    else
                    {
                        _state = new ProxyPoolState();
                    }
                }
            }

            var changed = !loadedExistingState && MergeSeedSources();
            _initialized = true;
            if (!File.Exists(_filePath) || changed || recoveredFromBackup)
            {
                if (primaryWasCorrupt)
                {
                    File.Delete(_filePath);
                }
                await SaveUnsafeAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> ReadAsync<T>(Func<ProxyPoolState, T> reader,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return reader(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> WriteAsync<T>(Func<ProxyPoolState, T> writer,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = writer(_state);
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveUnsafeAsync(cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool MergeSeedSources()
    {
        var changed = false;
        foreach (var seed in _options.Sources)
        {
            if (_state.Sources.Any(source =>
                    source.Url.Equals(seed.Url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _state.Sources.Add(new ProxySource
            {
                Name = seed.Name,
                Url = seed.Url,
                Protocol = seed.Protocol,
                Enabled = seed.Enabled,
                IsBuiltIn = true
            });
            changed = true;
        }

        return changed;
    }

    private async Task SaveUnsafeAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = $"{_filePath}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, _state, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                File.Replace(temporaryPath, _filePath, _backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _filePath);
                File.Copy(_filePath, _backupPath, true);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<ProxyPoolState> LoadStateAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProxyPoolState>(stream, _jsonOptions, cancellationToken)
               ?? new ProxyPoolState();
    }
}
