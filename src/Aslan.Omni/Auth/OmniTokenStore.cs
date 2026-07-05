using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aslan.Omni.Auth;

internal sealed record SavedToken(
    string TerminalId,
    string TokenId,
    string TokenSecret,   // base64-encoded raw secret
    string IpAddress,
    int Port,
    string PosId,
    DateTimeOffset PairedAt
);

/// <summary>
/// Persists pairing tokens as a JSON file on disk.
/// Thread-safe via SemaphoreSlim.
/// </summary>
internal sealed class OmniTokenStore : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<SavedToken>? _cache;

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public OmniTokenStore(string? path)
    {
        _path = path ?? DefaultPath();
    }

    public async Task<SavedToken?> GetAsync(string terminalId, CancellationToken ct = default)
    {
        var tokens = await LoadAsync(ct);
        return tokens.Find(t => t.TerminalId == terminalId);
    }

    public async Task<bool> ExistsAsync(string terminalId, CancellationToken ct = default)
        => await GetAsync(terminalId, ct) != null;

    public async Task SaveAsync(SavedToken token, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var tokens = await LoadLockedAsync(ct);
            tokens.RemoveAll(t => t.TerminalId == token.TerminalId);
            tokens.Add(token);
            _cache = tokens;
            await PersistAsync(tokens, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string terminalId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var tokens = await LoadLockedAsync(ct);
            tokens.RemoveAll(t => t.TerminalId == terminalId);
            _cache = tokens;
            await PersistAsync(tokens, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<SavedToken>> LoadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await LoadLockedAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Must be called while _lock is held.
    private async Task<List<SavedToken>> LoadLockedAsync(CancellationToken ct)
    {
        if (_cache != null) return _cache;
        if (!File.Exists(_path))
        {
            _cache = new List<SavedToken>();
            return _cache;
        }
#if NET48
        var json = File.ReadAllText(_path);
        _cache = JsonSerializer.Deserialize<List<SavedToken>>(json, _json) ?? new();
#else
        await using var fs = File.OpenRead(_path);
        _cache = await JsonSerializer.DeserializeAsync<List<SavedToken>>(fs, _json, ct) ?? new();
#endif
        return _cache;
    }

    private async Task PersistAsync(List<SavedToken> tokens, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
#if NET48
        File.WriteAllText(_path, JsonSerializer.Serialize(tokens, _json));
        await Task.CompletedTask;
#else
        await using var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await JsonSerializer.SerializeAsync(fs, tokens, _json, ct);
#endif
    }

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(
            Environment.OSVersion.Platform == PlatformID.Win32NT
                ? Environment.SpecialFolder.ApplicationData
                : Environment.SpecialFolder.UserProfile);

        return Environment.OSVersion.Platform == PlatformID.Win32NT
            ? Path.Combine(appData, "Aslan", "omni-tokens.json")
            : Path.Combine(appData, ".config", "aslan", "omni-tokens.json");
    }

    public void Dispose() => _lock.Dispose();
}
