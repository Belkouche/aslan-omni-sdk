using System.Net.Http.Json;
using Aslan.Omni;
using Aslan.Omni.Models;
using Aslan.Omni.Exceptions;

const string TargetIp   = "192.168.24.126";
const int    TargetPort = 8080;

// Optional: pass serial as first arg to skip the prompt
// Usage: dotnet run --project tests/Aslan.Omni.Console [serial]
var argSerial = args.Length > 0 ? args[0] : null;

var omni = new OmniClient();

// ── Discover terminal (UDP broadcast, 4s) ─────────────────────────────────────
// If the terminal responds, we get its device ID automatically.
// Falls back to prompting if UDP is blocked.
Console.WriteLine($"Scanning for terminal at {TargetIp}...");
OmniTerminal? terminal = null;

var found = await omni.DiscoverAsync(TimeSpan.FromSeconds(4));
terminal = found.FirstOrDefault(t => t.IpAddress == TargetIp);

if (terminal is null)
{
    Console.Error.WriteLine($"No terminal responded at {TargetIp}. Check WiFi and terminal app.");
    return 1;
}

// Android 10+ returns "unknown" for Build.SERIAL without READ_PRIVILEGED_PHONE_STATE.
// Fall back to using the IP as a stable terminal key (fine for a known static IP).
if (terminal.Id is "unknown" or "" or null)
{
    var fallbackId = argSerial ?? $"omni-{TargetIp}";
    Console.WriteLine($"Serial unavailable, using id={fallbackId}");
    terminal = terminal with { Id = fallbackId, IsPaired = await omni.HasTokenAsync(fallbackId) };
}

Console.WriteLine($"Terminal: {terminal.Id}  model={terminal.Model}  ip={terminal.IpAddress}  paired={terminal.IsPaired}");

// ── Verify reachability ───────────────────────────────────────────────────────
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
try
{
    var health = await http.GetFromJsonAsync<HealthResponse>($"http://{TargetIp}:{TargetPort}/health");
    Console.WriteLine($"Terminal health: status={health?.Status}  version={health?.Version}  napsPay={health?.NapsPay}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cannot reach terminal — {ex.Message}");
    return 1;
}

// ── Pair if needed ────────────────────────────────────────────────────────────
if (!terminal.IsPaired)
{
    Console.WriteLine("Not paired. Starting pairing flow...");
    try
    {
        var pair = await omni.PairAsync(
            terminal,
            posId:    "TEST-POS-01",
            posLabel: "SDK Integration Test",
            pinProvider: async () =>
            {
                Console.Write("PIN displayed on terminal screen: ");
                return ReadLine().Trim();
            });
        Console.WriteLine($"Paired. TokenId={pair.TokenId}");
        terminal = terminal with { IsPaired = true };
    }
    catch (OmniException ex)
    {
        Console.Error.WriteLine($"Pairing failed [{ex.Code}]: {ex.Message}");
        return 2;
    }
}

// ── Interactive menu ──────────────────────────────────────────────────────────
while (true)
{
    Console.WriteLine();
    Console.WriteLine("1  Pay");
    Console.WriteLine("2  List paired POS");
    Console.WriteLine("Q  Quit");
    Console.Write("> ");
    var choice = ReadLine().Trim().ToUpper();

    if (choice == "Q") break;

    if (choice == "1")
    {
        Console.Write("Amount in DH (e.g. 10.50): ");
        if (!decimal.TryParse(ReadLine(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dh) || dh <= 0)
        {
            Console.WriteLine("Invalid amount.");
            continue;
        }
        var amountCents = (int)Math.Round(dh * 100);
        var orderRef = $"TEST-{DateTime.UtcNow:yyyyMMddHHmmss}";

        Console.WriteLine($"Sending pay — ref={orderRef}  amount={amountCents} centimes ...");
        try
        {
            var result = await omni.PayAsync(terminal.Id, new PayRequest
            {
                Ref         = orderRef,
                AmountCents = amountCents,
                CashierId   = "SDK-TEST",
                TillId      = "TEST-POS-01",
                Description = "SDK integration test",
            });

            if (result.IsApproved)
            {
                Console.WriteLine("APPROVED");
                Console.WriteLine($"  Auth : {result.AuthNumber}");
                Console.WriteLine($"  STAN : {result.Stan}");
                Console.WriteLine($"  RRN  : {result.Rrn}");
                Console.WriteLine($"  Card : {result.CardNumber}  ({result.CardScheme})");
                Console.WriteLine($"  Date : {result.TransactionDate} {result.TransactionTime}");

                Console.Write("Reverse? [y/N]: ");
                if (ReadLine().Trim().ToLower() == "y" && result.Stan != null)
                {
                    var rev = await omni.ReverseAsync(terminal.Id, new ReversalRequest
                    {
                        Ref         = $"VOID-{orderRef}",
                        OriginalRef = orderRef,
                        Stan        = result.Stan,
                    });
                    Console.WriteLine(rev.IsReversed ? "Reversed OK" : $"Reversal failed RC={rev.ResponseCode}");
                }
            }
            else if (result.IsCancelled) Console.WriteLine("CANCELLED by cardholder");
            else if (result.IsDeclined)  Console.WriteLine($"DECLINED  RC={result.ResponseCode}  {result.ResponseMessage}");
            else if (result.IsTimeout)   Console.WriteLine("TIMEOUT — 90-second window elapsed");
            else                         Console.WriteLine($"Unknown status: {result.Status}");
        }
        catch (OmniException ex)
        {
            Console.Error.WriteLine($"Error [{ex.Code}]: {ex.Message}");
        }
    }

    if (choice == "2")
    {
        var posList = await omni.ListPosAsync(terminal.Id);
        if (posList.Count == 0) { Console.WriteLine("No paired POS found."); continue; }
        foreach (var p in posList)
            Console.WriteLine($"  {p.PosId,-20} {p.PosLabel,-25} paired={p.PairedAt:g}  token={p.TokenId}");
    }
}

return 0;

// Reads from /dev/tty directly so it works even when stdin is redirected/null.
static string ReadLine()
{
    try
    {
        using var tty = new StreamReader(new FileStream("/dev/tty", FileMode.Open, FileAccess.Read));
        return tty.ReadLine() ?? "";
    }
    catch
    {
        return Console.ReadLine() ?? "";
    }
}

record HealthResponse(string Status, string Version, string NapsPay);
