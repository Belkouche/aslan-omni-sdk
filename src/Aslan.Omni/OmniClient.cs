using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
#if NET48 || NETFRAMEWORK
using Aslan.Omni.Polyfills;
#endif
using System.Text.Json;
using System.Text.Json.Serialization;
using Aslan.Omni.Auth;
using Aslan.Omni.Exceptions;
using Aslan.Omni.Models;

namespace Aslan.Omni;

/// <summary>
/// Main entry point for the Aslan Omni POS SDK.
///
/// Typical usage:
/// <code>
/// var omni = new OmniClient();
///
/// var terminals = await omni.DiscoverAsync();
/// var terminal  = terminals.First();
///
/// if (!terminal.IsPaired)
///     await omni.PairAsync(terminal, "TILL-01", "Caisse 1",
///         async () => { Console.Write("PIN: "); return Console.ReadLine()!; });
///
/// var result = await omni.PayAsync(terminal.Id,
///     new PayRequest { Ref = "ORDER-001", AmountCents = 15000 });
/// </code>
/// </summary>
public sealed class OmniClient : IDisposable
{
    private readonly OmniClientOptions _options;
    private readonly OmniTokenStore _store;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public OmniClient(OmniClientOptions? options = null)
    {
        _options = options ?? new OmniClientOptions();
        _store = new OmniTokenStore(_options.TokenStorePath);

        if (_options.HttpClientFactory != null)
        {
            _http = _options.HttpClientFactory();
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient();
            _ownsHttp = true;
        }
    }

    // -------------------------------------------------------------------------
    // Discovery
    // -------------------------------------------------------------------------

    /// <summary>
    /// Broadcasts a UDP discovery packet and returns all responding Omni terminals.
    /// Already-paired terminals are flagged with <see cref="OmniTerminal.IsPaired"/>.
    /// </summary>
    public Task<IReadOnlyList<OmniTerminal>> DiscoverAsync(
        TimeSpan timeout = default,
        CancellationToken ct = default)
        => OmniDiscovery.ScanAsync(
            port: _options.DiscoveryPort,
            httpPort: _options.TerminalHttpPort,
            timeout: timeout,
            isPairedFunc: id => _store.ExistsAsync(id, ct),
            ct: ct);

    // -------------------------------------------------------------------------
    // Pairing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Executes the three-phase pairing flow:
    ///   1. POST /pair/request  → terminal displays a 6-digit PIN
    ///   2. <paramref name="pinProvider"/> is awaited (caller shows prompt)
    ///   3. POST /pair/confirm  → token issued and persisted locally
    /// </summary>
    /// <param name="terminal">Terminal returned from <see cref="DiscoverAsync"/>.</param>
    /// <param name="posId">Machine-readable POS identifier, e.g. "TILL-01".</param>
    /// <param name="posLabel">Human-readable label shown on the terminal screen, e.g. "Caisse 1".</param>
    /// <param name="pinProvider">
    ///   Async callback invoked after phase 1. Must return the 6-digit PIN
    ///   the cashier reads from the terminal screen.
    /// </param>
    public async Task<PairResult> PairAsync(
        OmniTerminal terminal,
        string posId,
        string posLabel,
        Func<Task<string>> pinProvider,
        CancellationToken ct = default)
    {
#if NET48 || NETFRAMEWORK
        var clientNonce = Net48Compat.ToHexString(RandomBytes(16));
#else
        var clientNonce = Convert.ToHexString(RandomBytes(16)).ToLowerInvariant();
#endif

        // Phase 1 — request
        var reqBody = new { posId, posLabel, clientNonce };
        var phase1 = await PostAsync<Phase1Response>(
            terminal.BaseUri, "/pair/request", reqBody, signed: false,
            timeout: _options.DefaultTimeout, ct: ct);

        // Phase 2 — caller reads PIN from terminal screen
        var pin = await pinProvider();
        if (string.IsNullOrWhiteSpace(pin) || pin.Length != 6)
            throw new OmniException(OmniErrorCode.PairingFailed, "PIN must be exactly 6 digits.");

        // Phase 3 — confirm
        var confirmBody = new { sessionId = phase1.SessionId, pin, posId, posLabel };
        var phase3 = await PostAsync<Phase3Response>(
            terminal.BaseUri, "/pair/confirm", confirmBody, signed: false,
            timeout: _options.DefaultTimeout, ct: ct);

        var saved = new SavedToken(
            TerminalId:  terminal.Id,
            TokenId:     phase3.TokenId,
            TokenSecret: phase3.TokenSecret,
            IpAddress:   terminal.IpAddress,
            Port:        terminal.Port,
            PosId:       posId,
            PairedAt:    DateTimeOffset.UtcNow
        );
        await _store.SaveAsync(saved, ct);

        return new PairResult
        {
            TokenId        = phase3.TokenId,
            TerminalId     = terminal.Id,
            TerminalSerial = phase3.TerminalSerial,
            TerminalModel  = phase3.TerminalModel,
            PosId          = posId,
        };
    }

    // -------------------------------------------------------------------------
    // Payment
    // -------------------------------------------------------------------------

    /// <summary>
    /// Initiates a card payment. Blocks until the cardholder approves or declines
    /// (up to 90 seconds on the terminal + network overhead).
    /// </summary>
    public async Task<PayResult> PayAsync(
        string terminalId,
        PayRequest request,
        CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(terminalId, ct);
        var body = new
        {
            @ref = request.Ref,
            amountCents = request.AmountCents,
            cashierId = request.CashierId,
            tillId = request.TillId,
            description = request.Description,
        };
        return await PostSignedAsync<PayResult>(token, "/pay", body, _options.PayTimeout, ct);
    }

    // -------------------------------------------------------------------------
    // Reversal
    // -------------------------------------------------------------------------

    public async Task<PayResult> ReverseAsync(
        string terminalId,
        ReversalRequest request,
        CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(terminalId, ct);
        var body = new
        {
            @ref = request.Ref,
            originalRef = request.OriginalRef,
            stan = request.Stan,
        };
        return await PostSignedAsync<PayResult>(token, "/reversal", body, _options.PayTimeout, ct);
    }

    // -------------------------------------------------------------------------
    // Admin
    // -------------------------------------------------------------------------

    /// <summary>Returns the list of POS tokens registered on the terminal.</summary>
    public async Task<IReadOnlyList<PosInfo>> ListPosAsync(
        string terminalId,
        CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(terminalId, ct);
        var uri = new Uri(TerminalBase(token), "/admin/pos");
        var resp = await SendSignedAsync(HttpMethod.Get, uri, token, null,
            _options.DefaultTimeout, ct);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var list = new List<PosInfo>();
        foreach (var item in doc.GetProperty("pos").EnumerateArray())
        {
            list.Add(new PosInfo
            {
                TokenId  = item.GetProperty("tokenId").GetString()!,
                PosId    = item.GetProperty("posId").GetString()!,
                PosLabel = item.GetProperty("posLabel").GetString()!,
                PairedAt = DateTimeOffset.Parse(item.GetProperty("pairedAt").GetString()!),
            });
        }
        return list;
    }

    /// <summary>
    /// Revokes a POS token on the terminal. The revoked POS will receive 401 on its next request.
    /// </summary>
    public async Task RevokePosAsync(
        string terminalId,
        string tokenId,
        CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(terminalId, ct);
        var uri = new Uri(TerminalBase(token), $"/admin/pos/{tokenId}");
        await SendSignedAsync(HttpMethod.Delete, uri, token, null,
            _options.DefaultTimeout, ct);
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private async Task<SavedToken> RequireTokenAsync(string terminalId, CancellationToken ct)
    {
        var token = await _store.GetAsync(terminalId, ct)
            ?? throw new OmniException(OmniErrorCode.Unauthorized,
                $"No pairing token found for terminal '{terminalId}'. Call PairAsync first.");
        return token;
    }

    private static Uri TerminalBase(SavedToken token) =>
        new($"http://{token.IpAddress}:{token.Port}");

    private async Task<T> PostSignedAsync<T>(
        SavedToken token,
        string path,
        object bodyObj,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var uri = new Uri(TerminalBase(token), path);
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(bodyObj, _json);
        var tokenSecret = Convert.FromBase64String(token.TokenSecret);
        var authHeader = OmniSigner.BuildHeader(token.TokenId, tokenSecret, "POST", path, bodyBytes);

        using var req = new HttpRequestMessage(HttpMethod.Post, uri);
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(authHeader);
        req.Content = new ByteArrayContent(bodyBytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, cts.Token); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new OmniException(OmniErrorCode.NetworkError, $"Network error calling {path}: {ex.Message}", inner: ex);
        }

        return await ParseResponseAsync<T>(resp, path);
    }

    private async Task<HttpResponseMessage> SendSignedAsync(
        HttpMethod method,
        Uri uri,
        SavedToken token,
        object? bodyObj,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var path = uri.AbsolutePath;
        var bodyBytes = bodyObj != null
            ? JsonSerializer.SerializeToUtf8Bytes(bodyObj, _json)
            : Array.Empty<byte>();
        var tokenSecret = Convert.FromBase64String(token.TokenSecret);
        var authHeader = OmniSigner.BuildHeader(token.TokenId, tokenSecret, method.Method, path, bodyBytes);

        using var req = new HttpRequestMessage(method, uri);
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(authHeader);
        if (bodyBytes.Length > 0)
        {
            req.Content = new ByteArrayContent(bodyBytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try { return await _http.SendAsync(req, cts.Token); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new OmniException(OmniErrorCode.NetworkError, $"Network error calling {path}: {ex.Message}", inner: ex);
        }
    }

    private async Task<T> PostAsync<T>(
        Uri baseUri,
        string path,
        object bodyObj,
        bool signed,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var uri = new Uri(baseUri, path);
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(bodyObj, _json);

        using var req = new HttpRequestMessage(HttpMethod.Post, uri);
        req.Content = new ByteArrayContent(bodyBytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, cts.Token); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new OmniException(OmniErrorCode.NetworkError, $"Network error calling {path}: {ex.Message}", inner: ex);
        }

        return await ParseResponseAsync<T>(resp, path);
    }

    private static async Task<T> ParseResponseAsync<T>(HttpResponseMessage resp, string path)
    {
        var raw = await resp.Content.ReadAsStringAsync();

        var code = (int)resp.StatusCode;
        if (!resp.IsSuccessStatusCode)
        {
            var errorCode = code switch
            {
                401 => OmniErrorCode.Unauthorized,
                404 => OmniErrorCode.NotFound,
                409 => OmniErrorCode.Conflict,
                410 => OmniErrorCode.PaymentTimeout,
                _ => OmniErrorCode.NetworkError,
            };
            var msg = TryExtractError(raw) ?? $"Terminal returned {code} for {path}";
            throw new OmniException(errorCode, msg, httpStatus: code);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw, _json)
                ?? throw new OmniException(OmniErrorCode.ProtocolError, $"Empty response body from {path}");
        }
        catch (JsonException ex)
        {
            throw new OmniException(OmniErrorCode.ProtocolError, $"Unexpected response from {path}: {ex.Message}", inner: ex);
        }
    }

    private static string? TryExtractError(string raw)
    {
        try
        {
            var doc = JsonDocument.Parse(raw).RootElement;
            return doc.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    private static byte[] RandomBytes(int count)
    {
#if NET48 || NETFRAMEWORK
        return Net48Compat.GetRandomBytes(count);
#else
        var buf = new byte[count];
        RandomNumberGenerator.Fill(buf);
        return buf;
#endif
    }

    public void Dispose()
    {
        _store.Dispose();
        if (_ownsHttp) _http.Dispose();
    }

    // DTO shapes used only for deserialization — kept private/internal
    private sealed class Phase1Response { public string SessionId { get; set; } = ""; }
    private sealed class Phase3Response
    {
        public string TokenId { get; set; } = "";
        public string TokenSecret { get; set; } = "";
        public string TerminalSerial { get; set; } = "";
        public string TerminalModel { get; set; } = "";
    }
}
