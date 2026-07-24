using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aslan.Omni.Exceptions;
using Aslan.Omni.Models;

namespace Aslan.Omni;

/// <summary>
/// Cloud-mode client for Aslan Omni terminals.
///
/// Uses <c>Authorization: Bearer &lt;apiKey&gt;</c> and <c>X-Device-Id</c> headers.
/// No UDP discovery, no OMNI-TOKEN pairing — credentials come from <see cref="PairAsync"/>.
///
/// Typical usage:
/// <code>
/// // One-time pairing (run interactively when the terminal shows a code):
/// var paired = await OmniCloudClient.PairAsync("ABCD1234");
///
/// // Normal usage:
/// var client = new OmniCloudClient(paired.ApiKey, paired.DeviceId);
/// var result = await client.PayAsync(new PayRequest { Ref = "ORDER-001", AmountCents = 15000 });
/// </code>
/// </summary>
public sealed class OmniCloudClient : IDisposable
{
    private const string DefaultApiUrl = "https://api.aslanpay.ma";

    private readonly string _apiKey;
    private readonly string _deviceId;
    private readonly string _apiUrl;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <param name="apiKey">Merchant API key obtained from <see cref="PairAsync"/>.</param>
    /// <param name="deviceId">Device ID obtained from <see cref="PairAsync"/>.</param>
    /// <param name="apiUrl">API base URL. Defaults to <c>https://api.aslanpay.ma</c>.</param>
    /// <param name="http">Optional shared <see cref="HttpClient"/>. If null, one is created and owned by this instance.</param>
    public OmniCloudClient(string apiKey, string deviceId, string? apiUrl = null, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _deviceId = deviceId;
        _apiUrl = (apiUrl ?? DefaultApiUrl).TrimEnd('/');

        if (http != null)
        {
            _http = http;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            _ownsHttp = true;
        }
    }

    // -------------------------------------------------------------------------
    // Pairing — static, no credentials required
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pairs with a terminal using the 8-character code shown on its screen.
    /// Returns the <see cref="CloudPairResult"/> containing the <c>apiKey</c> and <c>deviceId</c>
    /// to use for all subsequent calls.
    /// </summary>
    public static async Task<CloudPairResult> PairAsync(
        string pairingCode,
        string? apiUrl = null,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        var baseUrl = (apiUrl ?? DefaultApiUrl).TrimEnd('/');
        var ownsHttp = http == null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { pairingCode }, _json);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/cloud/pair");
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpResponseMessage resp;
            try { resp = await http.SendAsync(req, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                throw new OmniException(OmniErrorCode.NetworkError, $"Network error during pairing: {ex.Message}", inner: ex);
            }

            return await ParseResponseAsync<CloudPairResult>(resp, "/cloud/pair");
        }
        finally
        {
            if (ownsHttp) http.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // Payment
    // -------------------------------------------------------------------------

    /// <summary>
    /// Initiates a card payment via the cloud. Long-polls up to ~90 seconds.
    /// Throws <see cref="OmniException"/> with <see cref="OmniErrorCode.NotFound"/> if the terminal is offline.
    /// Throws <see cref="OmniException"/> with <see cref="OmniErrorCode.Conflict"/> if a payment is already in progress.
    /// On 408 timeout, returns a <see cref="PayResult"/> with <c>Status = "timeout"</c> and a <c>CommandId</c>
    /// you can poll with <see cref="PollResultAsync"/>.
    /// </summary>
    public async Task<PayResult> PayAsync(PayRequest request, CancellationToken ct = default)
    {
        var body = new
        {
            @ref = request.Ref,
            amountCents = request.AmountCents,
            cashierId = request.CashierId,
            tillId = request.TillId,
            description = request.Description,
        };
        return await PostCloudAsync<PayResult>("/cloud/pay", body, ct);
    }

    // -------------------------------------------------------------------------
    // Reversal
    // -------------------------------------------------------------------------

    /// <summary>Reverses a previously approved payment.</summary>
    public async Task<PayResult> ReverseAsync(ReversalRequest request, CancellationToken ct = default)
    {
        var body = new
        {
            @ref = request.Ref,
            originalRef = request.OriginalRef,
            stan = request.Stan,
        };
        return await PostCloudAsync<PayResult>("/cloud/reverse", body, ct);
    }

    // -------------------------------------------------------------------------
    // Device management
    // -------------------------------------------------------------------------

    /// <summary>Returns all devices paired to the merchant account.</summary>
    public async Task<IReadOnlyList<CloudDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "/cloud/devices");
        var resp = await SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            var msg = TryExtractError(raw) ?? $"Cloud API returned {code} for /cloud/devices";
            throw new OmniException(OmniErrorCode.NetworkError, msg, httpStatus: code);
        }

        try
        {
            var doc = JsonDocument.Parse(raw).RootElement;
            var arr = doc.GetProperty("devices");
            var list = new List<CloudDevice>();
            foreach (var item in arr.EnumerateArray())
                list.Add(JsonSerializer.Deserialize<CloudDevice>(item.GetRawText(), _json)!);
            return list;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new OmniException(OmniErrorCode.ProtocolError, $"Unexpected response from /cloud/devices: {ex.Message}", inner: ex);
        }
    }

    /// <summary>Revokes the device with <paramref name="deviceId"/> from the merchant account.</summary>
    public async Task RevokeDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        var path = $"/cloud/devices/{Uri.EscapeDataString(deviceId)}/revoke";
        using var req = BuildRequest(HttpMethod.Post, path);
        var resp = await SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync();
            var code = (int)resp.StatusCode;
            var msg = TryExtractError(raw) ?? $"Cloud API returned {code} for {path}";
            throw new OmniException(OmniErrorCode.NetworkError, msg, httpStatus: code);
        }
    }

    // -------------------------------------------------------------------------
    // Status polling
    // -------------------------------------------------------------------------

    /// <summary>
    /// Polls <c>GET /cloud/pay/{commandId}/status</c> every 3 seconds until status != "pending"
    /// or <paramref name="timeout"/> elapses.
    /// Returns null if the deadline is reached before a terminal result.
    /// </summary>
    public async Task<PayResult?> PollResultAsync(string commandId, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var path = $"/cloud/pay/{Uri.EscapeDataString(commandId)}/status";

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            using var req = BuildRequest(HttpMethod.Get, path);
            HttpResponseMessage resp;
            try { resp = await SendAsync(req, ct); }
            catch (OmniException) { throw; }

            if (!resp.IsSuccessStatusCode)
            {
                var raw = await resp.Content.ReadAsStringAsync();
                var code = (int)resp.StatusCode;
                var msg = TryExtractError(raw) ?? $"Cloud API returned {code} for {path}";
                throw new OmniException(OmniErrorCode.NetworkError, msg, httpStatus: code);
            }

            var result = await ParseResponseAsync<CloudStatusResponse>(resp, path);
            if (result.Status != "pending")
            {
                return new PayResult
                {
                    Status = result.Status ?? "timeout",
                    Ref = commandId,
                    CommandId = commandId,
                    ResponseCode = result.ResponseCode,
                    AuthNumber = result.AuthNumber,
                    Stan = result.Stan,
                    Rrn = result.Rrn,
                    CardNumber = result.CardNumber,
                    CardScheme = result.CardScheme,
                    TransactionDate = result.TxDate,
                    TransactionTime = result.TxTime,
                };
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private async Task<T> PostCloudAsync<T>(string path, object bodyObj, CancellationToken ct)
    {
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(bodyObj, _json);
        using var req = BuildRequest(HttpMethod.Post, path);
        req.Content = new ByteArrayContent(bodyBytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage resp;
        try { resp = await SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new OmniException(OmniErrorCode.NetworkError, $"Network error calling {path}: {ex.Message}", inner: ex);
        }

        // 503 → terminal not connected
        if ((int)resp.StatusCode == 503)
            throw new OmniException(OmniErrorCode.NotFound, "Terminal not connected", httpStatus: 503);

        // 408 → timeout — return a timeout PayResult with commandId if available
        if ((int)resp.StatusCode == 408)
        {
            var raw408 = await resp.Content.ReadAsStringAsync();
            string? commandId = null;
            try
            {
                var doc = JsonDocument.Parse(raw408).RootElement;
                if (doc.TryGetProperty("commandId", out var cid)) commandId = cid.GetString();
            }
            catch { /* ignore */ }

            return (T)(object)new PayResult
            {
                Status = "timeout",
                Ref = string.Empty,
                CommandId = commandId,
            };
        }

        return await ParseResponseAsync<T>(resp, path);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, $"{_apiUrl}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Headers.Add("X-Device-Id", _deviceId);
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try { return await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new OmniException(OmniErrorCode.NetworkError, $"Network error: {ex.Message}", inner: ex);
        }
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
                408 => OmniErrorCode.PaymentTimeout,
                503 => OmniErrorCode.NotFound,
                _ => OmniErrorCode.NetworkError,
            };
            var msg = TryExtractError(raw) ?? $"Cloud API returned {code} for {path}";
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

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // Internal DTO for polling status endpoint
    private sealed class CloudStatusResponse
    {
        public string? Status { get; set; }
        public string? ResponseCode { get; set; }
        public string? AuthNumber { get; set; }
        public string? Stan { get; set; }
        public string? Rrn { get; set; }
        public string? CardNumber { get; set; }
        public string? CardScheme { get; set; }
        public string? TxDate { get; set; }
        public string? TxTime { get; set; }
    }
}
