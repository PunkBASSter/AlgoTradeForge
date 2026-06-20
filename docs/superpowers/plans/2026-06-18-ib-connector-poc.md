# Interactive Brokers Paper-Trading Connector POC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a throwaway C# console spike that proves the full Interactive Brokers paper-trading path end-to-end from a Linux container: connect to a headless IB Gateway, stream + aggregate market data, and drive a market/limit/bracket order lifecycle to fills.

**Architecture:** Two containers via docker-compose — `ib-gateway` (gnzsnz image: IB Gateway + IBC + Xvfb + socat, auto-logs into a paper account) and `ib-poc` (.NET 10 console using the official TWS API `IBApi` C# source over a binary socket with an `EWrapper` callback model). The console is staged: each phase (connect → data → market order → limit+cancel → bracket → read-back) runs independently for incremental bring-up.

**Tech Stack:** C# / .NET 10, official TWS API (`IBApi` C# source, vendored), xUnit (one unit-test project for the pure aggregator), Docker + docker-compose, gnzsnz/ib-gateway image, IBC.

## Global Constraints

- Lives in `poc/ib-connector/`, **outside** `AlgoTradeForge.slnx` — never added to the main solution, CI, or any existing project.
- `net10.0` target framework (matches repo).
- Throwaway/disposable: no wiring into `ILiveConnector` / `IExchangeOrderClient` / `IInt64BarStrategy`; no Domain changes.
- **Paper trading only.** `TRADING_MODE=paper`, paper API port `4002`. No live/real-money path, no 2FA automation.
- `READ_ONLY_API=no` on the gateway or `placeOrder` is silently refused.
- Secrets (`TWS_USERID`, `TWS_PASSWORD`) come only from a gitignored `.env` — never committed, never in any tracked file.
- IBApi `EWrapper`/`Order`/callback signatures are **version-specific**: implement against the exact vendored TWS API version; let the compiler surface mismatches. Version-sensitive members flagged inline (`error`, `orderStatus`, `commissionReport`/`commissionAndFeesReport`, `decimal` sizes).
- Instrument: AAPL `STK` / `SMART` / `USD`, `PrimaryExch = "NASDAQ"`.

**Verification reality:** an implementing agent can run `dotnet build` and the `CandleAggregator` unit tests itself. The **live IB E2E phases (B–G) require paper credentials, a running Docker daemon, and (for real-time data) US market hours** — those are run by the user/operator. Each IB-dependent task therefore specifies the exact log lines that constitute "pass."

---

## File Structure

```
poc/ib-connector/
  README.md                     # run instructions, prerequisites, market-hours caveat, pitfalls
  docker-compose.yml            # ib-gateway + ib-poc, env_file: .env, depends_on
  .env.example                  # TWS_USERID=, TWS_PASSWORD=, TRADING_MODE=paper
  .gitignore                    # .env, bin/, obj/
  Dockerfile                    # multistage build of the C# client
  src/
    IbPoc.csproj                # net10.0 console; globs ibapi/*.cs
    ibapi/                      # vendored official IBApi C# source (compiled in)
    Logging.cs                  # static timestamped console logger (E2E evidence)
    CandleAggregator.cs         # TradeTick → fixed-width Candle folding (pure, unit-tested)
    DemoWrapper.cs              # EWrapper impl: used callbacks + TaskCompletionSource signals
    IbConnection.cs             # eConnect + EReader signal-thread pump + connect-retry + lifecycle
    Contracts.cs                # AAPL contract factory + conId resolution helper
    MarketData.cs               # reqMarketDataType / reqMktData / reqTickByTickData / reqRealTimeBars / historical
    Orders.cs                   # market, limit+cancel, bracket (parentId + Transmit flags)
    Program.cs                  # phase dispatcher: connect|data|market-order|limit-cancel|bracket|readback|all
  tests/
    IbPoc.Tests.csproj          # net10.0 xUnit; references src/IbPoc.csproj
    CandleAggregatorTests.cs
```

---

### Task 1: Scaffold the POC project skeleton

**Files:**
- Create: `poc/ib-connector/.gitignore`
- Create: `poc/ib-connector/.env.example`
- Create: `poc/ib-connector/src/IbPoc.csproj`
- Create: `poc/ib-connector/src/Program.cs`

**Interfaces:**
- Produces: a buildable `net10.0` console named `IbPoc` (assembly `IbPoc.dll`) with a phase-dispatching `Main`.

- [ ] **Step 1: Create `.gitignore`**

```gitignore
.env
bin/
obj/
ibapi/
```

> Note: `ibapi/` is gitignored because the IBApi source is a third-party download (Task 2), not our code to vendor into history. The README documents how to fetch it.

- [ ] **Step 2: Create `.env.example`**

```dotenv
# Copy to .env (gitignored) and fill in your IB *paper* credentials.
TWS_USERID=your_paper_username
TWS_PASSWORD=your_paper_password
TRADING_MODE=paper
# Optional: set to enable VNC into the gateway GUI for debugging.
VNC_SERVER_PASSWORD=
```

- [ ] **Step 3: Create `src/IbPoc.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>IbPoc</AssemblyName>
    <RootNamespace>IbPoc</RootNamespace>
    <!-- IBApi vendored source under ibapi/ is globbed in automatically by the SDK default includes. -->
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Create `src/Program.cs` (phase dispatcher stub)**

```csharp
namespace IbPoc;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var phase = args.Length > 0 ? args[0]
            : Environment.GetEnvironmentVariable("IB_PHASE") ?? "all";
        Console.WriteLine($"[ib-poc] starting phase '{phase}'");
        await Task.CompletedTask;
        return 0;
    }
}
```

- [ ] **Step 5: Build**

Run: `dotnet build poc/ib-connector/src/IbPoc.csproj`
Expected: `Build succeeded`.

- [ ] **Step 6: Commit**

```bash
git add poc/ib-connector/.gitignore poc/ib-connector/.env.example poc/ib-connector/src/IbPoc.csproj poc/ib-connector/src/Program.cs
git commit -m "chore(poc): scaffold IB connector spike skeleton"
```

---

### Task 2: Vendor the official IBApi C# source

**Files:**
- Create: `poc/ib-connector/src/ibapi/*.cs` (downloaded, not authored)
- Create: `poc/ib-connector/README.md` (fetch instructions section)

**Interfaces:**
- Produces: namespace `IBApi` types — `EClientSocket`, `EReader`, `EReaderMonitorSignal`, `EWrapper`, `Contract`, `Order`, `OrderState`, `Execution`, `Bar`, `TickAttrib`, `ContractDetails`, `CommissionReport` (or `CommissionAndFeesReport`) — compiled directly into `IbPoc`.

- [ ] **Step 1: Download the TWS API source**

Fetch the stable TWS API from https://interactivebrokers.github.io/ (the "Stable" API zip). After unpacking, the C# client source is at `IBJts/source/CSharpClient/client/`.

- [ ] **Step 2: Copy the client source in**

Copy every `*.cs` under `IBJts/source/CSharpClient/client/` into `poc/ib-connector/src/ibapi/` (preserve the files; they declare `namespace IBApi`). Record the downloaded version string (e.g. `10.30.01`) for Step 4.

- [ ] **Step 3: Build to confirm the source compiles under net10.0**

Run: `dotnet build poc/ib-connector/src/IbPoc.csproj`
Expected: `Build succeeded` (the IBApi source is plain C#, no Windows-only dependencies).

- [ ] **Step 4: Create `README.md` with the fetch instructions**

```markdown
# IB Connector POC

Throwaway spike: proves the Interactive Brokers paper-trading E2E path from a Linux container.
See `docs/superpowers/specs/2026-06-18-ib-connector-poc-design.md` for the design.

## Prerequisites
- An IB account with **paper trading** enabled (paper username/password).
- Real-time market-data subscription for US equities if you want real-time AAPL ticks;
  otherwise the POC falls back to delayed-frozen + historical data.
- Docker + docker-compose.

## One-time: vendor the IBApi source (gitignored, not committed)
1. Download the **Stable** TWS API from https://interactivebrokers.github.io/
2. Unpack and copy `IBJts/source/CSharpClient/client/*.cs` into `src/ibapi/`.
3. Vendored version: **<RECORD VERSION HERE>**.

## Run
1. `cp .env.example .env` and fill in your **paper** credentials.
2. `docker compose up --build`
3. Watch the logs for the verification sequence (see "Verification" in the plan/spec).

## ⚠️ Market hours
Real-time AAPL ticks only stream during US market hours (and extended hours if enabled).
Off-hours the POC uses `reqMarketDataType(4)` (delayed-frozen) and historical
`keepUpToDate` so the data/aggregation path still runs.
```

- [ ] **Step 5: Commit (README only — `ibapi/` is gitignored)**

```bash
git add poc/ib-connector/README.md
git commit -m "docs(poc): IBApi vendoring instructions + README"
```

---

### Task 3: CandleAggregator (pure logic, full TDD)

**Files:**
- Create: `poc/ib-connector/src/CandleAggregator.cs`
- Create: `poc/ib-connector/tests/IbPoc.Tests.csproj`
- Test: `poc/ib-connector/tests/CandleAggregatorTests.cs`

**Interfaces:**
- Produces:
  - `record TradeTick(long EpochMs, double Price, decimal Size)`
  - `record Candle(long BucketStartMs, double Open, double High, double Low, double Close, decimal Volume, int TickCount)`
  - `class CandleAggregator(int bucketSeconds)` with `Candle? Add(TradeTick tick)` (returns the just-completed prior candle when a tick rolls into a new bucket, else `null`) and `Candle? Flush()` (emits the in-progress candle, if any).

- [ ] **Step 1: Create the xUnit test project**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../src/IbPoc.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the failing tests**

```csharp
using IbPoc;
using Xunit;

public class CandleAggregatorTests
{
    [Fact]
    public void Add_TicksInSameBucket_ReturnsNullAndDoesNotEmit()
    {
        var agg = new CandleAggregator(bucketSeconds: 5);
        Assert.Null(agg.Add(new TradeTick(0, 10.0, 1)));
        Assert.Null(agg.Add(new TradeTick(2_000, 11.0, 2)));   // same 5s bucket [0,5000)
    }

    [Fact]
    public void Add_TickInNewBucket_EmitsCompletedPriorCandleWithOHLCV()
    {
        var agg = new CandleAggregator(bucketSeconds: 5);
        agg.Add(new TradeTick(0, 10.0, 1));
        agg.Add(new TradeTick(1_000, 12.0, 2));
        agg.Add(new TradeTick(2_000, 9.0, 3));
        var emitted = agg.Add(new TradeTick(5_000, 20.0, 1)); // rolls into bucket [5000,10000)
        Assert.NotNull(emitted);
        Assert.Equal(0, emitted!.BucketStartMs);
        Assert.Equal(10.0, emitted.Open);
        Assert.Equal(12.0, emitted.High);
        Assert.Equal(9.0, emitted.Low);
        Assert.Equal(9.0, emitted.Close);
        Assert.Equal(6m, emitted.Volume);
        Assert.Equal(3, emitted.TickCount);
    }

    [Fact]
    public void Flush_WithInProgressBucket_EmitsIt()
    {
        var agg = new CandleAggregator(bucketSeconds: 5);
        agg.Add(new TradeTick(0, 10.0, 1));
        var flushed = agg.Flush();
        Assert.NotNull(flushed);
        Assert.Equal(10.0, flushed!.Close);
        Assert.Null(agg.Flush()); // nothing left
    }
}
```

- [ ] **Step 3: Run tests, verify they fail to compile (type not defined)**

Run: `dotnet test poc/ib-connector/tests/IbPoc.Tests.csproj`
Expected: FAIL — `CandleAggregator`/`TradeTick`/`Candle` do not exist.

- [ ] **Step 4: Implement `CandleAggregator.cs`**

```csharp
namespace IbPoc;

public sealed record TradeTick(long EpochMs, double Price, decimal Size);

public sealed record Candle(
    long BucketStartMs, double Open, double High, double Low, double Close,
    decimal Volume, int TickCount);

public sealed class CandleAggregator(int bucketSeconds)
{
    private readonly long _bucketMs = bucketSeconds * 1000L;
    private long _bucketStart = -1;
    private double _open, _high, _low, _close;
    private decimal _volume;
    private int _count;

    public Candle? Add(TradeTick tick)
    {
        var bucket = (tick.EpochMs / _bucketMs) * _bucketMs;
        Candle? emitted = null;
        if (_bucketStart < 0)
        {
            StartBucket(bucket, tick);
            return null;
        }
        if (bucket != _bucketStart)
        {
            emitted = Snapshot();
            StartBucket(bucket, tick);
            return emitted;
        }
        _high = Math.Max(_high, tick.Price);
        _low = Math.Min(_low, tick.Price);
        _close = tick.Price;
        _volume += tick.Size;
        _count++;
        return emitted;
    }

    public Candle? Flush()
    {
        if (_bucketStart < 0) return null;
        var c = Snapshot();
        _bucketStart = -1;
        return c;
    }

    private void StartBucket(long bucket, TradeTick tick)
    {
        _bucketStart = bucket;
        _open = _high = _low = _close = tick.Price;
        _volume = tick.Size;
        _count = 1;
    }

    private Candle Snapshot() =>
        new(_bucketStart, _open, _high, _low, _close, _volume, _count);
}
```

- [ ] **Step 5: Run tests, verify pass**

Run: `dotnet test poc/ib-connector/tests/IbPoc.Tests.csproj`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add poc/ib-connector/src/CandleAggregator.cs poc/ib-connector/tests/
git commit -m "feat(poc): tick->candle aggregator with unit tests"
```

---

### Task 4: Logging helper

**Files:**
- Create: `poc/ib-connector/src/Logging.cs`

**Interfaces:**
- Produces: `static class Log` with `void Line(string msg)` (UTC-timestamped console write). Used everywhere as E2E evidence.

- [ ] **Step 1: Implement `Logging.cs`**

```csharp
namespace IbPoc;

internal static class Log
{
    public static void Line(string msg) =>
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {msg}");
}
```

- [ ] **Step 2: Build**

Run: `dotnet build poc/ib-connector/src/IbPoc.csproj`
Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add poc/ib-connector/src/Logging.cs
git commit -m "feat(poc): timestamped console logger"
```

---

### Task 5: DemoWrapper (EWrapper implementation)

**Files:**
- Create: `poc/ib-connector/src/DemoWrapper.cs`

**Interfaces:**
- Consumes: `IBApi` types from Task 2; `Log` from Task 4; `CandleAggregator`/`TradeTick` from Task 3.
- Produces: `class DemoWrapper : EWrapper` exposing:
  - `Task<int> NextValidIdAsync` (completes with the first valid order id)
  - `Task<int> ResolveConIdAsync(int reqId)` (completes when `contractDetails` for `reqId` arrives)
  - `event Action<TradeTick>? OnTrade` (fired from `tickByTickAllLast`)
  - `event Action<Candle>? OnRealtimeBar` (fired from `realtimeBar`, mapped to a `Candle`)
  - order-lifecycle TCS helpers: `Task WaitForStatusAsync(int orderId, string status)`.

- [ ] **Step 1: Implement the used callbacks + no-op the rest**

> Write the callbacks below in full. Implement **every other** `EWrapper` member as an empty body (`{ }`), matching the **exact signatures of your vendored IBApi version** — the compiler lists each missing member. Version-sensitive signatures to match carefully:
> - `error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)` plus the `error(Exception)` and `error(string)` overloads (older versions omit `errorTime`).
> - `orderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice, long permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)` (`permId` may be `int` in older versions).
> - `tickSize(int tickerId, int field, decimal size)` and `realtimeBar(..., decimal volume, decimal WAP, int count)` (`decimal` in recent versions).
> - `commissionReport(CommissionReport)` — in TWS API ≥ 10.30 this is `commissionAndFeesReport(CommissionAndFeesReport)`. Implement whichever your version declares and log it.

```csharp
using System.Collections.Concurrent;
using IBApi;

namespace IbPoc;

internal sealed partial class DemoWrapper : EWrapper
{
    private readonly TaskCompletionSource<int> _nextValidId =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<int>> _conIdByReq = new();
    private readonly ConcurrentDictionary<(int orderId, string status), TaskCompletionSource<bool>> _statusWaiters = new();

    public Task<int> NextValidIdAsync => _nextValidId.Task;
    public event Action<TradeTick>? OnTrade;
    public event Action<Candle>? OnRealtimeBar;

    public Task<int> ResolveConIdAsync(int reqId)
    {
        var tcs = _conIdByReq.GetOrAdd(reqId,
            _ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously));
        return tcs.Task;
    }

    public Task WaitForStatusAsync(int orderId, string status)
    {
        var tcs = _statusWaiters.GetOrAdd((orderId, status),
            _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        return tcs.Task;
    }

    public void connectAck() => Log.Line("connectAck — socket established");

    public void nextValidId(int orderId)
    {
        Log.Line($"nextValidId = {orderId}");
        _nextValidId.TrySetResult(orderId);
    }

    // Match your vendored signature; older versions omit errorTime.
    public void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        var informational = errorCode is 2104 or 2106 or 2158 or 2107 or 2108;
        Log.Line($"{(informational ? "info" : "ERROR")} id={id} code={errorCode} {errorMsg}");
    }
    public void error(Exception e) => Log.Line($"EXCEPTION {e.Message}");
    public void error(string str) => Log.Line($"ERROR {str}");

    public void contractDetails(int reqId, ContractDetails details)
    {
        Log.Line($"contractDetails req={reqId} conId={details.Contract.ConId} {details.Contract.LocalSymbol}");
        if (_conIdByReq.TryGetValue(reqId, out var tcs)) tcs.TrySetResult(details.Contract.ConId);
    }
    public void contractDetailsEnd(int reqId) { }

    public void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size,
        TickAttribLast tickAttribLast, string exchange, string specialConditions)
    {
        OnTrade?.Invoke(new TradeTick(time * 1000L, price, size));
    }

    public void realtimeBar(int reqId, long date, double open, double high, double low, double close,
        decimal volume, decimal WAP, int count)
    {
        OnRealtimeBar?.Invoke(new Candle(date * 1000L, open, high, low, close, volume, count));
    }

    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) =>
        Log.Line($"openOrder id={orderId} {order.Action} {order.OrderType} qty={order.TotalQuantity} state={orderState.Status}");

    public void orderStatus(int orderId, string status, decimal filled, decimal remaining,
        double avgFillPrice, long permId, int parentId, double lastFillPrice, int clientId,
        string whyHeld, double mktCapPrice)
    {
        Log.Line($"orderStatus id={orderId} status={status} filled={filled} avg={avgFillPrice}");
        if (_statusWaiters.TryGetValue((orderId, status), out var tcs)) tcs.TrySetResult(true);
    }

    public void execDetails(int reqId, Contract contract, Execution execution) =>
        Log.Line($"execDetails order={execution.OrderId} {execution.Side} {execution.Shares}@{execution.Price}");

    // TWS API >= 10.30: rename to commissionAndFeesReport(CommissionAndFeesReport).
    public void commissionReport(CommissionReport report) =>
        Log.Line($"commissionReport exec={report.ExecId} commission={report.Commission}");

    public void position(string account, Contract contract, decimal pos, double avgCost) =>
        Log.Line($"position {contract.Symbol} qty={pos} avgCost={avgCost}");
    public void positionEnd() => Log.Line("positionEnd");

    public void accountSummary(int reqId, string account, string tag, string value, string currency) =>
        Log.Line($"accountSummary {tag}={value} {currency}");
    public void accountSummaryEnd(int reqId) => Log.Line("accountSummaryEnd");

    // === Remaining EWrapper members: empty bodies, signatures per vendored version ===
    // e.g. public void managedAccounts(string accountsList) { }
    //      public void tickPrice(int tickerId, int field, double price, TickAttrib attribs) { }
    //      ... (compiler enumerates the full set)
}
```

> Tip: declaring `DemoWrapper` as `partial` lets you put the bulk of the empty no-op members in a second file `DemoWrapper.NoOps.cs` to keep the meaningful callbacks readable. Optional.

- [ ] **Step 2: Build**

Run: `dotnet build poc/ib-connector/src/IbPoc.csproj`
Expected: `Build succeeded` once every `EWrapper` member is implemented. Any "does not implement interface member" error names a callback still needing an empty body.

- [ ] **Step 3: Commit**

```bash
git add poc/ib-connector/src/DemoWrapper.cs
git commit -m "feat(poc): EWrapper implementation with lifecycle signals"
```

---

### Task 6: IbConnection (socket + reader pump + retry)

**Files:**
- Create: `poc/ib-connector/src/IbConnection.cs`

**Interfaces:**
- Consumes: `DemoWrapper`; `IBApi.EClientSocket`, `EReader`, `EReaderMonitorSignal`.
- Produces: `sealed class IbConnection : IAsyncDisposable` with:
  - ctor `(DemoWrapper wrapper, string host, int port, int clientId)`
  - `EClientSocket Client { get; }`
  - `Task ConnectAsync(int maxAttempts = 30, int retryDelayMs = 2000, CancellationToken ct = default)` — retries until the socket connects and the reader pump is running (handles the gateway-not-ready race), then awaits `wrapper.NextValidIdAsync`.
  - `void Disconnect()`

- [ ] **Step 1: Implement `IbConnection.cs`**

```csharp
using IBApi;

namespace IbPoc;

internal sealed class IbConnection : IAsyncDisposable
{
    private readonly DemoWrapper _wrapper;
    private readonly string _host;
    private readonly int _port;
    private readonly int _clientId;
    private readonly EReaderMonitorSignal _signal = new();
    private EClientSocket? _client;
    private Thread? _readerThread;

    public IbConnection(DemoWrapper wrapper, string host, int port, int clientId)
    {
        _wrapper = wrapper;
        _host = host;
        _port = port;
        _clientId = clientId;
    }

    public EClientSocket Client => _client ?? throw new InvalidOperationException("not connected");

    public async Task ConnectAsync(int maxAttempts = 30, int retryDelayMs = 2000, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _client = new EClientSocket(_wrapper, _signal);
            try
            {
                Log.Line($"eConnect {_host}:{_port} clientId={_clientId} (attempt {attempt}/{maxAttempts})");
                _client.eConnect(_host, _port, _clientId);
                if (_client.IsConnected())
                {
                    StartReaderPump(_client);
                    var orderId = await _wrapper.NextValidIdAsync.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    Log.Line($"connected; first orderId={orderId}");
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Line($"connect attempt failed: {e.Message}");
            }
            await Task.Delay(retryDelayMs, ct);
        }
        throw new TimeoutException($"could not connect to IB Gateway at {_host}:{_port}");
    }

    private void StartReaderPump(EClientSocket client)
    {
        var reader = new EReader(client, _signal);
        reader.Start();
        _readerThread = new Thread(() =>
        {
            while (client.IsConnected())
            {
                _signal.waitForSignal();
                reader.processMsgs();
            }
        }) { IsBackground = true, Name = "ib-ereader" };
        _readerThread.Start();
    }

    public void Disconnect()
    {
        if (_client?.IsConnected() == true)
        {
            Log.Line("eDisconnect");
            _client.eDisconnect();
        }
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build poc/ib-connector/src/IbPoc.csproj`
Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add poc/ib-connector/src/IbConnection.cs
git commit -m "feat(poc): IB socket connection with reader pump and connect-retry"
```

---

### Task 7: Contracts + Phase A/B wiring (connect → resolve contract)

**Files:**
- Create: `poc/ib-connector/src/Contracts.cs`
- Modify: `poc/ib-connector/src/Program.cs`

**Interfaces:**
- Consumes: `IbConnection`, `DemoWrapper`, `IBApi.Contract`.
- Produces:
  - `static class Contracts` with `Contract Aapl()` and `Task<int> ResolveAsync(IbConnection conn, DemoWrapper wrapper, Contract contract, int reqId)`.
  - `Program` helper `Task<(IbConnection, DemoWrapper)> ConnectAsync()` reading `IB_HOST`/`IB_PORT`/`IB_CLIENT_ID` env (defaults `ib-gateway`/`4002`/`10`), and phase routing for `connect`.

- [ ] **Step 1: Implement `Contracts.cs`**

```csharp
using IBApi;

namespace IbPoc;

internal static class Contracts
{
    public static Contract Aapl() => new()
    {
        Symbol = "AAPL",
        SecType = "STK",
        Exchange = "SMART",
        PrimaryExch = "NASDAQ",
        Currency = "USD",
    };

    public static async Task<int> ResolveAsync(IbConnection conn, DemoWrapper wrapper, Contract contract, int reqId)
    {
        var task = wrapper.ResolveConIdAsync(reqId);
        conn.Client.reqContractDetails(reqId, contract);
        var conId = await task.WaitAsync(TimeSpan.FromSeconds(15));
        contract.ConId = conId;
        return conId;
    }
}
```

- [ ] **Step 2: Wire connect/resolve into `Program.cs`**

```csharp
namespace IbPoc;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var phase = args.Length > 0 ? args[0]
            : Environment.GetEnvironmentVariable("IB_PHASE") ?? "all";
        Log.Line($"phase '{phase}'");

        var wrapper = new DemoWrapper();
        var host = Environment.GetEnvironmentVariable("IB_HOST") ?? "ib-gateway";
        var port = int.Parse(Environment.GetEnvironmentVariable("IB_PORT") ?? "4002");
        var clientId = int.Parse(Environment.GetEnvironmentVariable("IB_CLIENT_ID") ?? "10");

        await using var conn = new IbConnection(wrapper, host, port, clientId);
        await conn.ConnectAsync();

        if (phase == "connect") return 0;

        var contract = Contracts.Aapl();
        await Contracts.ResolveAsync(conn, wrapper, contract, reqId: 1);

        // later phases dispatched here (Tasks 8-12)
        return 0;
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build poc/ib-connector/src/IbPoc.csproj`
Expected: `Build succeeded`.

- [ ] **Step 4: Live verification (operator, requires gateway from Task 13)**

Once the gateway container is up (Task 13), run the client with `IB_PHASE=connect`.
Expected log sequence: `connectAck` → `nextValidId = N` → (contract phase prints `contractDetails ... conId=...`).
This proves Phase A (login) + Phase B (handshake) + contract resolution.

- [ ] **Step 5: Commit**

```bash
git add poc/ib-connector/src/Contracts.cs poc/ib-connector/src/Program.cs
git commit -m "feat(poc): connect handshake + AAPL contract resolution (phases A/B)"
```

---

### Task 8: MarketData + Phase C (stream + aggregate)

**Files:**
- Create: `poc/ib-connector/src/MarketData.cs`
- Modify: `poc/ib-connector/src/Program.cs`

**Interfaces:**
- Consumes: `IbConnection`, `DemoWrapper` (`OnTrade`, `OnRealtimeBar`), `CandleAggregator`, `IBApi.Contract`.
- Produces: `static class MarketData` with `Task StreamAsync(IbConnection conn, DemoWrapper wrapper, Contract contract, int aggSeconds, TimeSpan duration, bool realtime)`.

- [ ] **Step 1: Implement `MarketData.cs`**

```csharp
using IBApi;

namespace IbPoc;

internal static class MarketData
{
    public static async Task StreamAsync(IbConnection conn, DemoWrapper wrapper, Contract contract,
        int aggSeconds, TimeSpan duration, bool realtime)
    {
        // 1 = real-time, 4 = delayed-frozen (works off-hours / without a subscription).
        conn.Client.reqMarketDataType(realtime ? 1 : 4);

        var agg = new CandleAggregator(aggSeconds);
        wrapper.OnTrade += tick =>
        {
            var completed = agg.Add(tick);
            if (completed is not null)
                Log.Line($"AGG candle start={completed.BucketStartMs} O={completed.Open} H={completed.High} " +
                         $"L={completed.Low} C={completed.Close} V={completed.Volume} ticks={completed.TickCount}");
        };
        wrapper.OnRealtimeBar += bar =>
            Log.Line($"RTB 5s O={bar.Open} H={bar.High} L={bar.Low} C={bar.Close} V={bar.Volume}");

        const int tickReqId = 101, rtbReqId = 102, histReqId = 103;
        conn.Client.reqTickByTickData(tickReqId, contract, "AllLast", 0, false);
        conn.Client.reqRealTimeBars(rtbReqId, contract, 5, "TRADES", false, null);

        if (!realtime)
        {
            // Off-hours candle path: 1 day of 5s bars, streaming updates.
            conn.Client.reqHistoricalData(histReqId, contract, "", "1 D", "5 secs", "TRADES",
                useRTH: 0, formatDate: 2, keepUpToDate: true, chartOptions: null);
        }

        Log.Line($"streaming for {duration.TotalSeconds:0}s (realtime={realtime})");
        await Task.Delay(duration);

        conn.Client.cancelTickByTickData(tickReqId);
        conn.Client.cancelRealTimeBars(rtbReqId);
        if (!realtime) conn.Client.cancelHistoricalData(histReqId);
        var flushed = agg.Flush();
        if (flushed is not null) Log.Line($"AGG final candle C={flushed.Close} ticks={flushed.TickCount}");
    }
}
```

- [ ] **Step 2: Dispatch `data` phase in `Program.cs`**

Insert after contract resolution, before `return 0;`:

```csharp
        var realtime = (Environment.GetEnvironmentVariable("IB_REALTIME") ?? "true") == "true";
        if (phase is "data" or "all")
            await MarketData.StreamAsync(conn, wrapper, contract, aggSeconds: 10,
                duration: TimeSpan.FromSeconds(30), realtime);
        if (phase == "data") return 0;
```

- [ ] **Step 3: Build**

Run: `dotnet build poc/ib-connector/src/IbPoc.csproj`
Expected: `Build succeeded`. (Confirm `reqHistoricalData` parameter names match your vendored version.)

- [ ] **Step 4: Live verification (operator)**

Run with `IB_PHASE=data`. During US market hours (`IB_REALTIME=true`): expect `RTB 5s ...` lines and, on trade activity, `AGG candle ...` lines. Off-hours (`IB_REALTIME=false`): expect historical bars feeding the aggregator. Pass = at least one `AGG candle` or `RTB 5s` line.

- [ ] **Step 5: Commit**

```bash
git add poc/ib-connector/src/MarketData.cs poc/ib-connector/src/Program.cs
git commit -m "feat(poc): market data streaming + live aggregation (phase C)"
```

---

### Task 9: Orders — market round-trip (Phase D)

**Files:**
- Create: `poc/ib-connector/src/Orders.cs`
- Modify: `poc/ib-connector/src/Program.cs`

**Interfaces:**
- Consumes: `IbConnection`, `DemoWrapper.WaitForStatusAsync`, `IBApi.Order`, `IBApi.Contract`.
- Produces: `static class Orders` with `Task MarketRoundTripAsync(IbConnection conn, DemoWrapper wrapper, Contract contract, int orderId, decimal qty)`.

- [ ] **Step 1: Implement `Orders.cs` (market round-trip)**

```csharp
using IBApi;

namespace IbPoc;

internal static class Orders
{
    public static async Task MarketRoundTripAsync(IbConnection conn, DemoWrapper wrapper,
        Contract contract, int orderId, decimal qty)
    {
        var order = new Order { Action = "BUY", OrderType = "MKT", TotalQuantity = qty };
        var filled = wrapper.WaitForStatusAsync(orderId, "Filled");
        Log.Line($"placeOrder MKT BUY {qty} id={orderId}");
        conn.Client.placeOrder(orderId, contract, order);
        await filled.WaitAsync(TimeSpan.FromSeconds(30));
        Log.Line($"market order {orderId} filled");
    }
}
```

- [ ] **Step 2: Dispatch `market-order` phase in `Program.cs`**

```csharp
        var nextOrderId = await wrapper.NextValidIdAsync; // first valid id
        if (phase is "market-order" or "all")
            await Orders.MarketRoundTripAsync(conn, wrapper, contract, nextOrderId++, qty: 1);
        if (phase == "market-order") return 0;
```

- [ ] **Step 3: Build**

Run: `dotnet build poc/ib-connector/src/IbPoc.csproj`
Expected: `Build succeeded`.

- [ ] **Step 4: Live verification (operator)**

Run with `IB_PHASE=market-order`. Expect: `placeOrder MKT BUY ...` → `openOrder ...` → `orderStatus ... status=Filled` → `execDetails ...` → `commissionReport ...`. Pass = a `Filled` status for the order id.

- [ ] **Step 5: Commit**

```bash
git add poc/ib-connector/src/Orders.cs poc/ib-connector/src/Program.cs
git commit -m "feat(poc): market order round-trip to fill (phase D)"
```

---

### Task 10: Orders — limit + cancel (Phase E)

**Files:**
- Modify: `poc/ib-connector/src/Orders.cs`
- Modify: `poc/ib-connector/src/Program.cs`

**Interfaces:**
- Produces: `Orders.LimitThenCancelAsync(IbConnection conn, DemoWrapper wrapper, Contract contract, int orderId, decimal qty, double farLimitPrice)`.

- [ ] **Step 1: Add `LimitThenCancelAsync` to `Orders.cs`**

```csharp
    public static async Task LimitThenCancelAsync(IbConnection conn, DemoWrapper wrapper,
        Contract contract, int orderId, decimal qty, double farLimitPrice)
    {
        var order = new Order
        {
            Action = "BUY", OrderType = "LMT", TotalQuantity = qty, LmtPrice = farLimitPrice,
        };
        var submitted = wrapper.WaitForStatusAsync(orderId, "Submitted");
        var cancelled = wrapper.WaitForStatusAsync(orderId, "Cancelled");
        Log.Line($"placeOrder LMT BUY {qty} @ {farLimitPrice} id={orderId}");
        conn.Client.placeOrder(orderId, contract, order);
        await submitted.WaitAsync(TimeSpan.FromSeconds(20));
        Log.Line($"limit order {orderId} resting; cancelling");
        conn.Client.cancelOrder(orderId, ""); // match vendored cancelOrder overload (string time / OrderCancel)
        await cancelled.WaitAsync(TimeSpan.FromSeconds(20));
        Log.Line($"limit order {orderId} cancelled");
    }
```

> Version note: recent IBApi `cancelOrder(int id, string manualOrderCancelTime)`; some versions use `cancelOrder(int id, OrderCancel orderCancel)`. Match yours.

- [ ] **Step 2: Dispatch `limit-cancel` phase in `Program.cs`**

```csharp
        if (phase is "limit-cancel" or "all")
            await Orders.LimitThenCancelAsync(conn, wrapper, contract, nextOrderId++, qty: 1, farLimitPrice: 1.00);
        if (phase == "limit-cancel") return 0;
```

> `farLimitPrice: 1.00` is far below AAPL's market, so the order rests without filling.

- [ ] **Step 3: Build**

Run: `dotnet build poc/ib-connector/src/IbPoc.csproj`
Expected: `Build succeeded`.

- [ ] **Step 4: Live verification (operator)**

Run with `IB_PHASE=limit-cancel`. Expect: `placeOrder LMT ...` → `orderStatus ... status=Submitted` → `orderStatus ... status=Cancelled`. Pass = a `Cancelled` status for the order id.

- [ ] **Step 5: Commit**

```bash
git add poc/ib-connector/src/Orders.cs poc/ib-connector/src/Program.cs
git commit -m "feat(poc): resting limit order + cancel (phase E)"
```

---

### Task 11: Orders — bracket (entry + TP + SL) (Phase F)

**Files:**
- Modify: `poc/ib-connector/src/Orders.cs`
- Modify: `poc/ib-connector/src/Program.cs`

**Interfaces:**
- Produces: `Orders.PlaceBracketAsync(IbConnection conn, DemoWrapper wrapper, Contract contract, int parentId, decimal qty, double takeProfit, double stopLoss)`. Returns after all three orders are acknowledged via `openOrder`.

- [ ] **Step 1: Add `PlaceBracketAsync` to `Orders.cs`**

```csharp
    public static async Task PlaceBracketAsync(IbConnection conn, DemoWrapper wrapper,
        Contract contract, int parentId, decimal qty, double takeProfit, double stopLoss)
    {
        // Standard IB bracket: parent transmits last so children attach atomically.
        var parent = new Order
        {
            OrderId = parentId, Action = "BUY", OrderType = "MKT",
            TotalQuantity = qty, Transmit = false,
        };
        var tp = new Order
        {
            OrderId = parentId + 1, Action = "SELL", OrderType = "LMT",
            TotalQuantity = qty, LmtPrice = takeProfit, ParentId = parentId, Transmit = false,
        };
        var sl = new Order
        {
            OrderId = parentId + 2, Action = "SELL", OrderType = "STP",
            TotalQuantity = qty, AuxPrice = stopLoss, ParentId = parentId, Transmit = true,
        };

        var parentSubmitted = wrapper.WaitForStatusAsync(parentId, "PreSubmitted");
        Log.Line($"placeOrder BRACKET parent={parentId} TP={takeProfit} SL={stopLoss}");
        conn.Client.placeOrder(parent.OrderId, contract, parent);
        conn.Client.placeOrder(tp.OrderId, contract, tp);
        conn.Client.placeOrder(sl.OrderId, contract, sl);
        await Task.WhenAny(parentSubmitted, Task.Delay(TimeSpan.FromSeconds(20)));
        Log.Line($"bracket {parentId} submitted (parent + TP + SL)");
    }
```

> `Transmit=false` on parent + TP and `true` on the final child (SL) is the canonical IB bracket idiom — TWS links them and auto-OCAs the two children. This mirrors the repo's `TakeProfitLevels`/`StopLossPrice` shape.

- [ ] **Step 2: Dispatch `bracket` phase in `Program.cs`**

```csharp
        if (phase is "bracket" or "all")
        {
            await Orders.PlaceBracketAsync(conn, wrapper, contract, nextOrderId, qty: 1,
                takeProfit: 10_000.0, stopLoss: 1.0);   // far-from-market so nothing fills during the spike
            nextOrderId += 3;
        }
        if (phase == "bracket") return 0;
```

- [ ] **Step 3: Build**

Run: `dotnet build poc/ib-connector/src/IbPoc.csproj`
Expected: `Build succeeded`.

- [ ] **Step 4: Live verification (operator)**

Run with `IB_PHASE=bracket`. Expect three `openOrder` lines (parent MKT, child LMT, child STP) and `orderStatus` transitions. Pass = parent + both children appear in `openOrder`.

- [ ] **Step 5: Commit**

```bash
git add poc/ib-connector/src/Orders.cs poc/ib-connector/src/Program.cs
git commit -m "feat(poc): bracket order entry+TP+SL (phase F)"
```

---

### Task 12: Read-back + clean disconnect + full "all" sequence (Phase G)

**Files:**
- Create: `poc/ib-connector/src/AccountReadback.cs`
- Modify: `poc/ib-connector/src/Program.cs`

**Interfaces:**
- Produces: `static class AccountReadback` with `Task DumpAsync(IbConnection conn, DemoWrapper wrapper)` (positions + account summary).

- [ ] **Step 1: Implement `AccountReadback.cs`**

```csharp
namespace IbPoc;

internal static class AccountReadback
{
    public static async Task DumpAsync(IbConnection conn, DemoWrapper wrapper)
    {
        const int summaryReqId = 9001;
        conn.Client.reqPositions();
        conn.Client.reqAccountSummary(summaryReqId, "All", "NetLiquidation,TotalCashValue,BuyingPower");
        await Task.Delay(TimeSpan.FromSeconds(5));
        conn.Client.cancelAccountSummary(summaryReqId);
        conn.Client.cancelPositions();
    }
}
```

- [ ] **Step 2: Wire `readback` phase + final disconnect in `Program.cs`**

```csharp
        if (phase is "readback" or "all")
            await AccountReadback.DumpAsync(conn, wrapper);

        conn.Disconnect();
        Log.Line("done");
        return 0;
```

- [ ] **Step 3: Build**

Run: `dotnet build poc/ib-connector/src/IbPoc.csproj`
Expected: `Build succeeded`.

- [ ] **Step 4: Live verification (operator)**

Run with `IB_PHASE=readback`. Expect `position ...` / `positionEnd` and `accountSummary ...` lines, then `eDisconnect` + `done`. Pass = account summary + clean disconnect.

- [ ] **Step 5: Commit**

```bash
git add poc/ib-connector/src/AccountReadback.cs poc/ib-connector/src/Program.cs
git commit -m "feat(poc): position/account read-back + clean disconnect (phase G)"
```

---

### Task 13: Docker packaging + compose + README finalize

**Files:**
- Create: `poc/ib-connector/Dockerfile`
- Create: `poc/ib-connector/docker-compose.yml`
- Modify: `poc/ib-connector/README.md`

**Interfaces:**
- Produces: a runnable two-container stack; `docker compose up --build` brings up the gateway and runs the client.

- [ ] **Step 1: Create `Dockerfile` (multistage)**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ ./src/
RUN dotnet publish src/IbPoc.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "IbPoc.dll"]
```

> The vendored `src/ibapi/` is copied with `src/` and compiled in. `tests/` is intentionally not copied — it is not part of the runtime image.

- [ ] **Step 2: Create `docker-compose.yml`**

```yaml
services:
  ib-gateway:
    image: ghcr.io/gnzsnz/ib-gateway:stable
    restart: "no"
    env_file: .env
    environment:
      TWS_USERID: ${TWS_USERID}
      TWS_PASSWORD: ${TWS_PASSWORD}
      TRADING_MODE: ${TRADING_MODE:-paper}
      READ_ONLY_API: "no"
      VNC_SERVER_PASSWORD: ${VNC_SERVER_PASSWORD:-}
    ports:
      - "127.0.0.1:5900:5900"   # VNC (debug only); API port stays on the internal network
  ib-poc:
    build:
      context: .
      dockerfile: Dockerfile
    depends_on:
      - ib-gateway
    env_file: .env
    environment:
      IB_HOST: ib-gateway
      IB_PORT: "4002"
      IB_CLIENT_ID: "10"
      IB_PHASE: "${IB_PHASE:-all}"
      IB_REALTIME: "${IB_REALTIME:-true}"
    command: ["${IB_PHASE:-all}"]
```

> `depends_on` only waits for container start, not API readiness — `IbConnection.ConnectAsync` retries to absorb the gateway's login/startup delay.

- [ ] **Step 3: Finalize README run/verification section**

Append to `README.md`:

```markdown
## Verification (E2E definition of done)
With market open and real-time data: `IB_PHASE=all docker compose up --build`. Confirm, in order:
1. gateway/IBC logs a successful **paper login** (VNC into :5900 to watch the GUI if needed)
2. client logs `connectAck` → `nextValidId = N`
3. `contractDetails ... conId=...` for AAPL
4. `RTB 5s ...` and/or `AGG candle ...` lines
5. market order: `placeOrder MKT` → `orderStatus ... status=Filled` → `commissionReport`
6. limit: `orderStatus ... status=Submitted` → `status=Cancelled`
7. bracket: three `openOrder` lines (parent + TP + SL)
8. `accountSummary ...` + `eDisconnect` + `done`

Run a single phase with e.g. `IB_PHASE=connect docker compose up --build ib-poc`.
Off-hours, set `IB_REALTIME=false` to exercise the data/aggregation path via delayed + historical data.

## Pitfalls
- `READ_ONLY_API=no` is required for order placement.
- Paper API port is 4002 (live would be 4001).
- `clientId` must be unique per concurrent connection; a stale gateway session can block reconnects.
- Real-time equity ticks need a market-data subscription; otherwise use `IB_REALTIME=false`.
- IBApi callback signatures vary by version — this POC targets the vendored version recorded above.
```

- [ ] **Step 4: Validate compose config (no live login needed)**

Run: `cd poc/ib-connector && docker compose config`
Expected: the merged config prints with both services and no error.

- [ ] **Step 5: Full live E2E (operator, market hours)**

Run: `cd poc/ib-connector && cp .env.example .env` (fill paper creds) then `IB_PHASE=all docker compose up --build`.
Expected: the 8-step sequence above appears in the logs. Capture the log as the artifact proving the path.

- [ ] **Step 6: Commit**

```bash
git add poc/ib-connector/Dockerfile poc/ib-connector/docker-compose.yml poc/ib-connector/README.md
git commit -m "feat(poc): docker packaging + compose stack + run/verify docs"
```

---

## Self-Review

**Spec coverage:**
- Connect/handshake → Tasks 6–7. ✓
- Contract resolution → Task 7. ✓
- Ticks + real-time bars + historical fallback + aggregation → Tasks 3 (logic) + 8 (wiring). ✓
- Market order to fill → Task 9. ✓
- Limit + cancel → Task 10. ✓
- Bracket (entry/TP/SL) → Task 11. ✓
- Positions/account read-back + clean disconnect → Task 12. ✓
- Gateway+IBC sidecar + client container + compose + .env + README pitfalls → Tasks 1, 2, 13. ✓
- All "Key Pitfalls" from the spec are surfaced in the README (Task 13 Step 3). ✓
- Out-of-scope items (no `ILiveConnector` wiring, no JSONL relay, paper-only) respected — no task touches Domain or the main solution. ✓

**Placeholder scan:** The only deliberately non-literal parts are (a) the vendored IBApi source (a third-party download, with exact fetch path given) and (b) the no-op `EWrapper` members (compiler-enumerated, signature recipe given). These are inherent to wrapping a 150-method versioned vendor interface, not plan gaps. No "TBD/handle errors/similar to Task N" placeholders.

**Type consistency:** `DemoWrapper` members (`NextValidIdAsync`, `ResolveConIdAsync`, `OnTrade`, `OnRealtimeBar`, `WaitForStatusAsync`) are defined in Task 5 and consumed with matching names/types in Tasks 7–12. `TradeTick`/`Candle`/`CandleAggregator` defined in Task 3, consumed in Task 8. `IbConnection.Client`/`ConnectAsync`/`Disconnect` defined in Task 6, consumed in Tasks 7–12. `Contracts.Aapl()`/`ResolveAsync` defined in Task 7. `nextOrderId` threaded consistently through Program phases 9–11. Consistent. ✓
