using System.Net.Http;

namespace Aslan.Omni.Models;

public class OmniClientOptions
{
    /// <summary>
    /// Path to the JSON file where paired terminal tokens are persisted.
    /// Defaults to %APPDATA%\Aslan\omni-tokens.json on Windows,
    /// ~/.config/aslan/omni-tokens.json on Linux/macOS.
    /// </summary>
    public string? TokenStorePath { get; set; }

    /// <summary>UDP port Omni terminals listen on for discovery broadcasts. Default 8481.</summary>
    public int DiscoveryPort { get; set; } = 8481;

    /// <summary>HTTP port Omni terminals expose their REST API on. Default 8080.</summary>
    public int TerminalHttpPort { get; set; } = 8080;

    /// <summary>
    /// Timeout for pay/reversal requests. Should exceed the terminal's 90-second NapsPay timeout.
    /// Default 95 seconds.
    /// </summary>
    public TimeSpan PayTimeout { get; set; } = TimeSpan.FromSeconds(95);

    /// <summary>Timeout for all non-payment requests (pairing, list, revoke). Default 10 seconds.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Supply a custom HttpClient factory for testing or when integrating into an
    /// existing IHttpClientFactory setup. When null, the SDK creates its own client.
    /// </summary>
    public Func<HttpClient>? HttpClientFactory { get; set; }
}
