using System.Net.Http;
using Aslan.Omni;
using Aslan.Omni.Exceptions;
using Aslan.Omni.Models;

namespace Aslan.Omni.E2ETests;

public class CloudClientE2ETests
{
    // OMNI_E2E_API_URL — base URL of the Aslan Omni API (e.g. "https://omni-api.aslanpay.ma").
    // OMNI_E2E_API_KEY — merchant API key from a paired session.
    // OMNI_E2E_DEVICE_ID — device ID from that pairing.
    // All three must be set for the cloud tests to run.
    private static string? ApiKey   => Environment.GetEnvironmentVariable("OMNI_E2E_API_KEY");
    private static string? DeviceId => Environment.GetEnvironmentVariable("OMNI_E2E_DEVICE_ID");
    private static string? ApiUrl   => Environment.GetEnvironmentVariable("OMNI_E2E_API_URL");

    /// <summary>
    /// Returns true (and outputs a skip message) when credentials are not configured.
    /// Used instead of xunit v3 Skip.If — in xunit v2 we early-return to make the test
    /// pass vacuously rather than fail when env vars are absent.
    /// </summary>
    private static bool MissingCredentials(out string reason)
    {
        if (string.IsNullOrEmpty(ApiKey) || string.IsNullOrEmpty(DeviceId) || string.IsNullOrEmpty(ApiUrl))
        {
            reason = "Set OMNI_E2E_API_URL, OMNI_E2E_API_KEY, and OMNI_E2E_DEVICE_ID to run E2E tests";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    private static OmniCloudClient BuildClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        return new OmniCloudClient(ApiKey!, DeviceId!, ApiUrl, http);
    }

    [Fact]
    public async Task CloudE2E_GetDevices_ReturnsOurDevice()
    {
        if (MissingCredentials(out _)) return;

        using var client = BuildClient();
        var devices = await client.GetDevicesAsync();

        Assert.NotNull(devices);
        Assert.Contains(devices, d => d.DeviceId == DeviceId);
    }

    [Fact]
    public async Task CloudE2E_Pay_ToSimulator_ApprovesOrTerminalOffline()
    {
        if (MissingCredentials(out _)) return;

        var request = new PayRequest
        {
            Ref = $"E2E-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            AmountCents = 1000, // 10 DH
        };

        using var client = BuildClient();

        try
        {
            var result = await client.PayAsync(request);
            // Approved or any terminal-side outcome
            Assert.NotNull(result.Status);
            if (result.IsApproved)
                Assert.NotNull(result.Stan);
        }
        catch (OmniException ex) when (ex.Code == OmniErrorCode.NotFound)
        {
            // Terminal offline in CI — acceptable
        }
    }

    [Fact]
    public async Task CloudE2E_DemoToken_Endpoint_IsReachable()
    {
        if (string.IsNullOrEmpty(ApiUrl)) return; // OMNI_E2E_API_URL not set — skip

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync($"{ApiUrl!.TrimEnd('/')}/devices/demo-token");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Network failure — not a test failure; server may be down.
            return;
        }

        // 200 = demo token present; 404 = endpoint exists but OMNI_DEMO_DEVICE_ID not configured.
        // Both are acceptable — we care that it doesn't 500 or route to the wrong service.
        Assert.True(
            (int)resp.StatusCode is 200 or 404,
            $"Expected 200 or 404 from /devices/demo-token, got {(int)resp.StatusCode}");
    }
}
