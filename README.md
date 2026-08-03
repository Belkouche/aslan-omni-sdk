# Aslan.Omni — C# SDK for Aslan Omni Terminals

C# SDK for POS applications that need to process card payments through an **Aslan Omni** terminal on the local network.

Aslan Omni is a foreground service running on Sunmi P2/P3 terminals. It exposes a local HTTP API (port 8080) that any POS, kiosk, or cashier app on the same LAN can call to trigger card payments via NapsPay — without any internet dependency.

**Targets:** .NET 9, .NET 10, .NET Framework 4.8

---

## Installation

`Aslan.Omni` is distributed as a **private NuGet package** on GitHub Packages (not
public nuget.org). Ask Aslan for access, then add the feed and the package.

1. Create a `nuget.config` next to your solution:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="aslan-github" value="https://nuget.pkg.github.com/Belkouche/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <aslan-github>
      <add key="Username" value="YOUR_GITHUB_USERNAME" />
      <!-- A GitHub Personal Access Token with the read:packages scope -->
      <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
    </aslan-github>
  </packageSourceCredentials>
</configuration>
```

2. Install:

```bash
dotnet add package Aslan.Omni
```

> Store the token in an environment variable (`GITHUB_PACKAGES_TOKEN`), not in the
> committed `nuget.config`.

---

## How it works — big picture

```
┌─────────────┐   discover (UDP)   ┌─────────────────┐
│  POS / App  │ ─────────────────► │   Aslan Omni    │
│ (this SDK)  │ ◄───────────────── │  Sunmi P2 / P3  │
│             │   pair (HTTP/PIN)  │                 │
│             │ ──────────────────►│                 │
│             │   pay  (HTTP)      │   ┌──────────┐  │
│             │ ──────────────────►│   │ NapsPay  │  │
│             │ ◄────────────────  │   │ (card)   │  │
└─────────────┘   result (JSON)    │   └──────────┘  │
                                   │   ┌──────────┐  │
                                   │   │  Printer │  │
                                   │   └──────────┘  │
                                   └─────────────────┘
```

There are two independent flows:

1. **Local flow** — discovery, pairing, payment, reversal. Works entirely on LAN with no internet.
2. **Cloud flow** — the terminal registers itself on the Aslan dashboard; completed transactions are uploaded for reporting. Optional, can be enabled from the terminal's settings screen.

### Attended POS vs. kiosk / vending

Your integration code is the same in both cases — the SDK API does not change:

- **Attended POS** (a cashier reads the PIN, taps the card): works out of the box.
- **Kiosk / vending / unattended**: the terminal can be provisioned so payments are
  driven fully by your app with no operator. The Omni terminal handles the payment
  path automatically (including terminals configured in NapsPay **ECR mode**), so an
  approved payment is never auto-reversed. From the SDK you still just call
  `PayAsync` / `ReverseAsync` — no extra steps. If you are deploying into vending or
  self-service, tell Aslan so the terminal fleet is provisioned for unattended use.

---

## Quick start

```csharp
using Aslan.Omni;
using Aslan.Omni.Models;

// 1 — discover terminals on the LAN (3-second broadcast)
var omni = new OmniClient();
var terminals = await omni.DiscoverAsync(TimeSpan.FromSeconds(3));
var terminal = terminals.FirstOrDefault()
    ?? throw new Exception("No Aslan Omni terminal found on the network");

// 2 — pair once (first time only)
if (!terminal.IsPaired)
{
    await omni.PairAsync(terminal, posId: "TILL-01", posLabel: "Caisse 1",
        pinProvider: async () =>
        {
            Console.Write("PIN displayed on terminal: ");
            return Console.ReadLine()!;
        });
    Console.WriteLine("Paired successfully.");
}

// 3 — pay
var result = await omni.PayAsync(terminal.Id, new PayRequest
{
    Ref         = $"ORDER-{DateTime.UtcNow:yyyyMMddHHmmss}",
    AmountCents = 15000,   // 150,00 MAD
    CashierId   = "CAISSIER-01",
    TillId      = "TILL-01",
    Description = "Table 7",
});

if (result.IsApproved)
    Console.WriteLine($"Approved — Auth: {result.AuthNumber}  STAN: {result.Stan}");
else if (result.IsCancelled)
    Console.WriteLine("Cancelled by cardholder");
else
    Console.WriteLine($"Declined — RC: {result.ResponseCode}");
```

---

## Onboarding — terminal pairing

Pairing is required once per POS device. The token is saved locally (JSON file) and reused automatically on every subsequent call.

### How the 3-phase pairing flow works

```
POS (SDK)                           Aslan Omni terminal
────────────────────────────────────────────────────────
POST /pair/request  ─────────────►  generates 6-digit PIN
  { posId, posLabel }               displays PIN full-screen
◄──────────────────  { sessionId }

[cashier reads PIN from terminal screen and types it into the POS UI]

POST /pair/confirm  ─────────────►  validates PIN + session
  { sessionId, pin, posId }         stores HMAC(tokenSecret)
◄──────────────────  { tokenId,      never stores raw secret
                       tokenSecret } 

SDK saves (tokenId, tokenSecret, terminalId, ip) to disk
```

The raw `tokenSecret` is never stored on the terminal — only its HMAC. If the POS machine is lost, its token can be revoked from any other paired POS.

> **🔒 Encrypted pairing (since v1.1.0).** `PairAsync` generates an ephemeral RSA key and sends its public key in `/pair/request`; the terminal returns the token secret **encrypted (RSA-OAEP-SHA256)** so it never crosses the LAN in cleartext. Automatic — **no code change**. On **.NET Framework 4.8** the SPKI/OAEP APIs aren't available, so net48 keeps the legacy cleartext path (net9/net10 are encrypted). See the migration note below.

#### Migrating from v1.0.0

**Nothing breaks and no re-pairing is needed.** Terminals paired with v1.0.0 keep working — the stored token is identical; the change only protects how the secret is delivered *during pairing*.

- **Upgrading the package is the whole fix** — `dotnet add package Aslan.Omni --version 1.1.0`, no code change. Every new pairing on net9/net10 is then encrypted.
- **If you stay on v1.0.0**, pairing still works but the token secret is sent in **cleartext** on the LAN — upgrading is recommended for the security fix.
- A v1.1.0 SDK still pairs with an older cleartext-only terminal (automatic fallback).

### Pairing in code

```csharp
var omni = new OmniClient();

// Discover
var terminals = await omni.DiscoverAsync();

// Pick the right terminal (in a real app, show a selection UI)
var terminal = terminals.Single(t => t.Model == "P2");

// Pair — PinProvider is called after phase 1, when the PIN appears on screen
var pair = await omni.PairAsync(
    terminal,
    posId:    "SELF-CHECKOUT-03",
    posLabel: "Caisse automatique 3",
    pinProvider: async () =>
    {
        Console.Write("PIN affiché sur le terminal : ");
        return Console.ReadLine()!;
    });

Console.WriteLine($"Paired. Token: {pair.TokenId}");
```

### Where tokens are stored

By default:
- **Windows:** `%APPDATA%\Aslan\omni-tokens.json`
- **Linux / macOS:** `~/.config/aslan/omni-tokens.json`

Override via `OmniClientOptions.TokenStorePath`:

```csharp
var omni = new OmniClient(new OmniClientOptions
{
    TokenStorePath = @"C:\POS\config\omni-tokens.json",
});
```

### Managing paired POS devices

```csharp
// List all POS tokens registered on the terminal
var posList = await omni.ListPosAsync(terminal.Id);
foreach (var pos in posList)
    Console.WriteLine($"{pos.PosId}  ({pos.PosLabel})  paired {pos.PairedAt:g}  token={pos.TokenId}");

// Revoke a specific POS (e.g. decommissioned till)
await omni.RevokePosAsync(terminal.Id, tokenId: pos.TokenId);
```

---

## Request authentication

Every payment request is signed with HMAC-SHA256. The SDK does this automatically — you never touch the header manually.

```
Authorization: OMNI-TOKEN tokenId=<uuid>,ts=<unix-seconds>,sig=<hex>

sig = HMAC-SHA256(tokenSecret, "{tokenId}:{ts}:{METHOD}:{path}:{body-sha256-hex}")
```

The terminal validates:
1. `tokenId` exists in its local database
2. `ts` is within ±30 seconds of the terminal clock (replay protection)
3. `sig` matches the stored HMAC

If any check fails → `401 Unauthorized` → `OmniException(OmniErrorCode.Unauthorized)`.

> **Clock skew — important.** The ±30 s window is checked against the **terminal's**
> clock. If the terminal's date/time is wrong (drifted, or auto-time disabled),
> every request fails with `401 Unauthorized` / `"timestamp out of window"` even
> though the token is valid. If you hit persistent 401s that pairing doesn't fix,
> enable **automatic date & time** on the terminal (Android Settings → System →
> Date & time). The POS machine's own clock should also be network-synced.

---

## Payment

### Simple payment

```csharp
var result = await omni.PayAsync(terminal.Id, new PayRequest
{
    Ref         = "ORDER-20260704-001",   // idempotency key, max 40 chars
    AmountCents = 15000,                  // 150,00 MAD
});

switch (result.Status)
{
    case "approved":
        Console.WriteLine($"Approved");
        Console.WriteLine($"  Auth     : {result.AuthNumber}");
        Console.WriteLine($"  STAN     : {result.Stan}");
        Console.WriteLine($"  RRN      : {result.Rrn}");
        Console.WriteLine($"  Card     : {result.CardNumber}  ({result.CardScheme})");
        Console.WriteLine($"  Terminal : {result.TerminalId}");
        Console.WriteLine($"  Merchant : {result.MerchantId}");
        Console.WriteLine($"  Date     : {result.TransactionDate}  {result.TransactionTime}");
        break;

    case "declined":
        Console.WriteLine($"Declined — RC={result.ResponseCode}  {result.ResponseMessage}");
        break;

    case "cancelled":
        Console.WriteLine("Cancelled by cardholder");
        break;

    case "timeout":
        Console.WriteLine("No response from terminal within 90 seconds");
        break;
}
```

### Payment with receipt fields

```csharp
var result = await omni.PayAsync(terminal.Id, new PayRequest
{
    Ref         = "ORDER-20260704-002",
    AmountCents = 8500,
    CashierId   = "CAISSIER-03",   // printed on receipt
    TillId      = "TILL-02",       // printed on receipt
    Description = "Table 12",      // printed on receipt
});
```

### Idempotency

The `Ref` field is an idempotency key. If the same `Ref` is sent:

- While already in-progress → `409 Conflict` → `OmniException(OmniErrorCode.Conflict)`
- After completion → the cached result is returned (no duplicate charge)

> **On the cache window:** completed results are currently kept with **no fixed
> expiry** — a same-`Ref` replay returns the original result as long as the record
> exists server-side. Don't build on that as a permanent guarantee, though: treat a
> `Ref` as reconcilable **within the same business day** and settle timeouts promptly,
> in case a retention/cleanup policy is introduced later.

Use a unique ref per transaction. A good pattern: `$"ORDER-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"`.

### Timeout behaviour & reconciliation

The terminal allows ~90 s for the cardholder; the SDK default is 95 s (`PayTimeout`). On timeout
you get a normal `PayResult` with `IsTimeout == true` (it does **not** throw).

**A timeout is not a failure — the card may still have been charged.** Never credit the order
(or reverse) on a raw timeout. Reconcile first.

**The rule that keeps you safe: reuse the same `Ref`.** `Ref` is a strict idempotency key —
there's only ever one command per `Ref` server-side — so re-calling `PayAsync` with the *same*
`Ref` **never double-charges**:

```csharp
var request = new PayRequest { Ref = "ORDER-001", AmountCents = 15000 };

var result = await omni.PayAsync(terminal.Id, request);
while (result.IsTimeout)
{
    // SAME request.Ref — never generate a new one. The server won't double-charge;
    // it returns the real outcome once the terminal has one.
    result = await omni.PayAsync(terminal.Id, request);
}
// result is now approved / declined / cancelled
```

- **Completed** → re-`PayAsync` returns the **original result** (same `AuthNumber`, `Stan`).
- **Timed out** → the server re-sends the command to the terminal; no duplicate.
- Prefer to just *check* without re-prompting the terminal? Poll the status — the timeout
  `PayResult` carries a `CommandId`:

```csharp
var settled = await omni.PollResultAsync(result.CommandId!, TimeSpan.FromMinutes(2));
```

> ⚠️ **Never generate a new `Ref` to retry** — that's the only way to create a real double
> charge. Same `Ref` = safe. If a charge is confirmed but unwanted, `ReverseAsync` it (below).

### Cancellation token

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(100));
var result = await omni.PayAsync(terminal.Id, request, cts.Token);
```

---

## Reversal (void)

Reverses an approved transaction. Requires the `Stan` from the original payment.

```csharp
// Original payment
var pay = await omni.PayAsync(terminal.Id, new PayRequest
{
    Ref         = "ORDER-001",
    AmountCents = 15000,
});

// Reverse it
if (pay.IsApproved && pay.Stan != null)
{
    var reversal = await omni.ReverseAsync(terminal.Id, new ReversalRequest
    {
        Ref         = "VOID-ORDER-001",   // new ref for this reversal
        OriginalRef = "ORDER-001",
        Stan        = pay.Stan,
    });

    Console.WriteLine(reversal.IsReversed ? "Reversed" : $"Reversal failed RC={reversal.ResponseCode}");
}
```

---

## Cloud mode — dashboard synchronisation

Cloud mode is optional and independent of local payments. It lets the terminal appear in the Aslan dashboard, and uploads each completed transaction for reporting.

### How it works

```
Terminal (Aslan Omni app)          Aslan Dashboard
──────────────────────────────────────────────────────────────
POST /api/omni/devices/claim ─────►  creates claim record
  { serialNumber, model, version }  returns claimUrl (UUID)

  [Terminal displays QR of claimUrl]

  [Admin opens claimUrl in browser, selects merchant, clicks Activate]

GET  /api/omni/devices/claim/      ◄─── admin activation
     {uuid}/status  (poll every 3s)
◄──────────────────  { status: "activated",
                       deviceToken, merchantId }

Terminal stores deviceToken in DataStore
Cloud icon turns green in the Omni app

── After each approved payment ───────────────────────────────
POST /api/omni/transactions ──────►  stored in dashboard DB
  { ref, amountCents, authNumber,    visible in "Terminaux Omni"
    stan, cardScheme, ... }
```

**Cloud onboarding is done entirely from the Aslan Omni Android app** — the POS SDK has no role in it. The SDK only communicates with the terminal over LAN.

---

## Discovery

### Automatic (recommended)

```csharp
var terminals = await omni.DiscoverAsync(TimeSpan.FromSeconds(3));
```

The SDK broadcasts `OMNI-DISC:<nonce>` on UDP port 8481. Each Omni terminal on the LAN replies with its device ID, model, and version. Terminals for which a pairing token already exists are flagged `IsPaired = true`.

### Static scan (without an OmniClient instance)

```csharp
var terminals = await OmniDiscovery.ScanAsync(timeout: TimeSpan.FromSeconds(5));
```

### Direct IP (no broadcast)

If UDP broadcast is blocked on your network, instantiate `OmniClient` and call methods directly using the known IP. Store the terminal info yourself:

```csharp
var terminal = new OmniTerminal
{
    Id        = "SUNMI-P2-SN1234ABCD",
    IpAddress = "192.168.1.105",
    Port      = 8080,
    Model     = "P2",
    Version   = "1.0.0",
    IsPaired  = true,
};

var result = await omni.PayAsync(terminal.Id, request);
```

---

## Error handling

All SDK errors throw `OmniException`. Check `Code` for the category:

```csharp
try
{
    var result = await omni.PayAsync(terminal.Id, request);
}
catch (OmniException ex)
{
    switch (ex.Code)
    {
        case OmniErrorCode.Unauthorized:
            // Token rejected — re-pair this terminal
            Console.WriteLine("Token invalid. Re-pairing required.");
            break;

        case OmniErrorCode.Conflict:
            // Same Ref already in-progress on the terminal
            Console.WriteLine("Duplicate Ref — transaction already running.");
            break;

        case OmniErrorCode.PaymentTimeout:
            // Terminal returned 410 — 90-second window elapsed
            Console.WriteLine("Payment window expired.");
            break;

        case OmniErrorCode.NetworkError:
            // TCP/HTTP error — terminal unreachable
            Console.WriteLine($"Network error: {ex.Message}");
            break;

        case OmniErrorCode.PairingFailed:
            // Wrong PIN, expired session, or rate-limited
            Console.WriteLine("Pairing failed. Check PIN and retry.");
            break;

        case OmniErrorCode.NotFound:
            Console.WriteLine("Terminal not found or returned 404.");
            break;

        case OmniErrorCode.ProtocolError:
            Console.WriteLine($"Unexpected response: {ex.Message}");
            break;
    }
}
```

| Code | HTTP status | Meaning |
|---|---|---|
| `Unauthorized` | 401 | Token invalid or revoked — re-pair |
| `NotFound` | 404 | Terminal unreachable or endpoint missing |
| `Conflict` | 409 | Same `Ref` already in-progress |
| `PaymentTimeout` | 410 | 90-second NapsPay window expired |
| `PairingFailed` | — | Wrong PIN, expired session, rate-limited |
| `NetworkError` | — | TCP / HTTP failure |
| `ProtocolError` | — | Unexpected response shape |

---

## Full example — complete POS session

```csharp
using Aslan.Omni;
using Aslan.Omni.Models;
using Aslan.Omni.Exceptions;

var omni = new OmniClient(new OmniClientOptions
{
    PayTimeout     = TimeSpan.FromSeconds(95),
    DefaultTimeout = TimeSpan.FromSeconds(10),
});

// ── Discover ──────────────────────────────────────────────────────────────────
Console.WriteLine("Scanning for Aslan Omni terminals...");
var terminals = await omni.DiscoverAsync(TimeSpan.FromSeconds(4));
if (terminals.Count == 0)
{
    Console.WriteLine("No terminal found. Check WiFi.");
    return;
}

var terminal = terminals[0];
Console.WriteLine($"Found: {terminal.Id}  ({terminal.Model})  {terminal.IpAddress}");

// ── Pair (first time only) ────────────────────────────────────────────────────
if (!terminal.IsPaired)
{
    Console.WriteLine("Terminal not paired. Starting pairing...");
    try
    {
        var pair = await omni.PairAsync(
            terminal,
            posId:    "TILL-01",
            posLabel: "Caisse 1",
            pinProvider: async () =>
            {
                Console.Write("PIN displayed on terminal: ");
                return Console.ReadLine()!;
            });
        Console.WriteLine($"Paired. Token: {pair.TokenId}");
    }
    catch (OmniException ex) when (ex.Code == OmniErrorCode.PairingFailed)
    {
        Console.WriteLine($"Pairing failed: {ex.Message}");
        return;
    }
}

// ── Payment ──────────────────────────────────────────────────────────────────
var orderRef = $"ORDER-{DateTime.UtcNow:yyyyMMddHHmmss}";

PayResult pay;
try
{
    pay = await omni.PayAsync(terminal.Id, new PayRequest
    {
        Ref         = orderRef,
        AmountCents = 25000,          // 250,00 MAD
        CashierId   = "CAISSIER-01",
        TillId      = "TILL-01",
        Description = "Commande #42",
    });
}
catch (OmniException ex)
{
    Console.WriteLine($"Payment error [{ex.Code}]: {ex.Message}");
    return;
}

if (pay.IsApproved)
{
    Console.WriteLine("Payment approved");
    Console.WriteLine($"  Auth     : {pay.AuthNumber}");
    Console.WriteLine($"  STAN     : {pay.Stan}");
    Console.WriteLine($"  Card     : {pay.CardNumber}  {pay.CardScheme}");
    Console.WriteLine($"  Amount   : {pay.AmountCents / 100m:F2} MAD");
    Console.WriteLine($"  Date     : {pay.TransactionDate} {pay.TransactionTime}");

    // ── Reversal example (optional) ───────────────────────────────────────
    Console.Write("Reverse this payment? [y/N] ");
    if (Console.ReadLine()?.Trim().ToLower() == "y" && pay.Stan != null)
    {
        try
        {
            var reversal = await omni.ReverseAsync(terminal.Id, new ReversalRequest
            {
                Ref         = $"VOID-{orderRef}",
                OriginalRef = orderRef,
                Stan        = pay.Stan,
            });
            Console.WriteLine(reversal.IsReversed
                ? "Reversed successfully"
                : $"Reversal failed RC={reversal.ResponseCode}");
        }
        catch (OmniException ex)
        {
            Console.WriteLine($"Reversal error: {ex.Message}");
        }
    }
}
else if (pay.IsCancelled)
{
    Console.WriteLine("Cancelled by cardholder");
}
else if (pay.IsDeclined)
{
    Console.WriteLine($"Declined — RC={pay.ResponseCode}  {pay.ResponseMessage}");
}
else if (pay.IsTimeout)
{
    Console.WriteLine("Timeout — no response from terminal within 90 s");
}

// ── Admin — list / revoke POS ─────────────────────────────────────────────────
Console.WriteLine("\nPaired POS on this terminal:");
var posList = await omni.ListPosAsync(terminal.Id);
foreach (var pos in posList)
    Console.WriteLine($"  {pos.PosId,-20} {pos.PosLabel,-25} paired {pos.PairedAt:g}  token={pos.TokenId}");

// Revoke a decommissioned till:
// await omni.RevokePosAsync(terminal.Id, tokenId: "<tokenId>");
```

---

## API reference

### `OmniClient`

```csharp
new OmniClient(OmniClientOptions? options = null)

// Discovery
Task<IReadOnlyList<OmniTerminal>> DiscoverAsync(TimeSpan timeout = default, CancellationToken ct = default)

// Pairing
Task<PairResult> PairAsync(OmniTerminal terminal, string posId, string posLabel,
    Func<Task<string>> pinProvider, CancellationToken ct = default)

// Transactions
Task<PayResult>  PayAsync(string terminalId, PayRequest request, CancellationToken ct = default)
Task<PayResult>  ReverseAsync(string terminalId, ReversalRequest request, CancellationToken ct = default)

// Admin
Task<IReadOnlyList<PosInfo>> ListPosAsync(string terminalId, CancellationToken ct = default)
Task RevokePosAsync(string terminalId, string tokenId, CancellationToken ct = default)

void Dispose()
```

### `OmniClientOptions`

| Property | Type | Default | Description |
|---|---|---|---|
| `TokenStorePath` | `string?` | OS default | Path to the JSON token file |
| `DiscoveryPort` | `int` | `8481` | UDP port for discovery broadcasts |
| `TerminalHttpPort` | `int` | `8080` | HTTP port on the terminal |
| `PayTimeout` | `TimeSpan` | 95 s | Timeout for pay / reversal calls |
| `DefaultTimeout` | `TimeSpan` | 10 s | Timeout for all other calls |
| `HttpClientFactory` | `Func<HttpClient>?` | `null` | Custom HttpClient (for testing / DI) |

### `PayRequest`

| Property | Type | Required | Description |
|---|---|---|---|
| `Ref` | `string` | ✓ | Idempotency key — max 40 chars |
| `AmountCents` | `int` | ✓ | Amount in Moroccan centimes |
| `CashierId` | `string?` | | Printed on receipt |
| `TillId` | `string?` | | Printed on receipt |
| `Description` | `string?` | | Printed on receipt |

### `PayResult`

| Property | Type | Description |
|---|---|---|
| `Status` | `string` | `"approved"` `"declined"` `"cancelled"` `"timeout"` `"reversed"` |
| `IsApproved` | `bool` | `Status == "approved"` |
| `IsDeclined` | `bool` | `Status == "declined"` |
| `IsCancelled` | `bool` | `Status == "cancelled"` |
| `IsTimeout` | `bool` | `Status == "timeout"` |
| `IsReversed` | `bool` | `Status == "reversed"` |
| `Ref` | `string` | Echo of request `Ref` |
| `ResponseCode` | `string?` | ISO 8583 code — `"000"` on approval |
| `ResponseMessage` | `string?` | Human-readable decline reason |
| `AuthNumber` | `string?` | 6-digit authorisation number |
| `Stan` | `string?` | System Trace Audit Number — keep for reversals |
| `Rrn` | `string?` | Retrieval Reference Number |
| `CardNumber` | `string?` | Masked PAN — e.g. `476173******4521` |
| `CardScheme` | `string?` | `"VISA"` `"MASTERCARD"` etc. |
| `TerminalId` | `string?` | NAPS terminal ID |
| `MerchantId` | `string?` | NAPS merchant ID |
| `TransactionDate` | `string?` | `ddMMyyyy` |
| `TransactionTime` | `string?` | `HHmmss` |
| `AmountCents` | `int?` | Confirmed amount in centimes |

### `ReversalRequest`

| Property | Type | Required | Description |
|---|---|---|---|
| `Ref` | `string` | ✓ | New ref for this reversal |
| `OriginalRef` | `string` | ✓ | Ref of the original payment |
| `Stan` | `string` | ✓ | STAN from the approved payment |

### `OmniTerminal`

| Property | Type | Description |
|---|---|---|
| `Id` | `string` | Stable device ID — e.g. `SUNMI-P2-SN1234ABCD` |
| `IpAddress` | `string` | Current IP on the LAN |
| `Port` | `int` | HTTP API port (normally 8080) |
| `Model` | `string` | `"P2"` or `"P3"` |
| `Version` | `string` | Omni app version |
| `IsPaired` | `bool` | Local token exists for this terminal |
| `BaseUri` | `Uri` | `http://{IpAddress}:{Port}` |

### `PairResult`

| Property | Type | Description |
|---|---|---|
| `TokenId` | `string` | UUID of the issued token |
| `TerminalId` | `string` | Device ID — use as key for subsequent calls |
| `TerminalSerial` | `string` | Hardware serial number |
| `TerminalModel` | `string` | Model string |
| `PosId` | `string` | The `posId` you supplied |

---

## Thread safety

`OmniClient` is not thread-safe. One transaction at a time per instance. The terminal itself serialises requests — a second `/pay` while one is in-progress returns `409 Conflict`.

---

## Build & test

```bash
dotnet build
dotnet test
dotnet pack src/Aslan.Omni -c Release
```

---

## License

Proprietary — Aslan Fintech / ASLANPAY SARL
