# LiveHost Plan 1 — Venue-neutral Contract Identity — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish Interactive Brokers contract identity as the root of the IB venue slice — a vendored IBApi, a long-term IB transport primitive, a two-tier `IbContract` model, a polymorphic `Asset → IbContract` mapper (equities + futures), and a real conId resolver (with futures front-month selection) validated against live paper IB.

**Architecture:** A new vendored `AlgoTradeForge.IbApi` library (TWS API 10.45.01, nullable-off) is referenced by `AlgoTradeForge.LiveHost.Infrastructure`. All IB types live in a new slice `LiveHost.Infrastructure/Live/InteractiveBrokers/` (namespace `AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers`). The Domain `Asset` hierarchy is reused for economics/strategy polymorphism and mapped to the venue-owned `IbContract` via a `GetSettlementCalculator`-style extension. A caching `IbContractResolver` sits over an `IIbContractDetailsClient` seam; the seam's real impl drives `reqContractDetails` over an `IbConnection` (the transport primitive Plan 3 will grow `IbSession` around), accumulating all returned contracts until `contractDetailsEnd` and selecting one (single for STK, front-month for FUT).

**Tech Stack:** C# 14 / .NET 10, xUnit v3, NSubstitute, `TimeProvider`, vendored IBApi 10.45.01, Google.Protobuf 3.29.5.

**Spec:** `docs/superpowers/specs/2026-06-26-livehost-plan1-contract-identity-design.md`

## Global Constraints

- **Domain stays venue-neutral, zero IB vocabulary, zero new ProjectReferences.** No `ConId`/`SecType`/`SMART`/`Currency`/`LocalSymbol`/expiry in Domain. All IB types live in the Infrastructure slice or the vendored IBApi lib.
- **`LiveHost.Infrastructure` may reference `AlgoTradeForge.IbApi`; Domain/Application may not.**
- One type per file (Constitution v1.9.0); extension methods in their own file alongside the type they extend. (A *private nested* helper type inside one class is allowed — it is not a top-level type.)
- No `Async` suffix on new async methods. `using`-over-`try`/`finally`. `CancellationToken ct = default` on async I/O.
- ONE `dotnet` process at a time — build/test strictly sequential, never parallel. `powershell.exe`, never `pwsh`.
- Test framework: **xUnit v3** (`using Xunit;`, `[Fact]`/`[Theory]`, `Assert.*`, `Assert.Skip(reason)`), **NSubstitute** (`Substitute.For<T>()`). No FluentAssertions.
- **Vendored IBApi is third-party — not ours to maintain:** its csproj sets `<Nullable>disable</Nullable>`, `<ImplicitUsings>disable</ImplicitUsings>`, and `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` (the root props force `true` solution-wide; override only for the vendored lib).
- **Commits:** the SDD implementer subagent's `git add` is hook-denied. The **controller stages + commits per task after task-review passes** (per-branch owner authorization). Commit messages via bash heredoc + `git commit -F`, ending with the `Co-Authored-By` + `Claude-Session` trailers. Owner squashes/merges — no PR without explicit say-so.
- **Branch:** `feat/livehost-plan1-contract-identity` off `main`.

**Scope note (asset kinds):** Forward mapping `Asset → IbContract` implements **`EquityAsset → STK`** and **`FutureAsset → FUT`** (commodity/metal/index futures, resolved to the front month). `CryptoAsset` (IB crypto is `PAXOS`-routed, distinct from a Binance-spot asset) and `CryptoPerpetualAsset` (no IB equivalent) are fenced with `NotSupportedException`. Options and single-stock futures are nice-to-haves, deferred (no Domain `OptionAsset` exists yet). Reverse mapping `IbContract → Asset` implements **STK only**; `FUT` reverse needs `contractDetails` multiplier/minTick enrichment (deferred to Plan 2/4 reconciliation, spec open point #3).

---

## File Structure

**New vendored library** (`src/AlgoTradeForge.IbApi/`):
- `IBApi.csproj` — nullable-off, implicit-usings-off, warnings-not-errors, Google.Protobuf 3.29.5.
- `*.cs` (≈68) + `protobuf/*.cs` — vendored TWS API 10.45.01 source (copied from `poc/ib-connector/ibapi/`).

**IB slice** (`src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/`, namespace `…Live.InteractiveBrokers`):
- `IbSecType.cs` — enum `{ Stk, Fut }`.
- `IbSecTypeExtensions.cs` — `ToIbString()` / `FromIbString()`.
- `IbContract.cs` — configured-tier record (5 fields).
- `ResolvedIbContract.cs` — resolved-tier record (`Spec`, `ConId`, `LocalSymbol`, `LastTradeDate`).
- `IbContractMapping.cs` — `ToIbContract(this Asset)` + `ToAsset(this ResolvedIbContract)`.
- `IbContractTranslation.cs` — `ToIbApiContract(this IbContract)` (→ `IBApi.Contract`).
- `IbContractDetailsResult.cs` — internal record (`ConId`, `LocalSymbol`, `LastTradeDate`).
- `IbRequestException.cs` — carries IB error code/message for a failed request.
- `IbWrapper.cs` — `: DefaultEWrapper`; `nextValidId` + `contractDetails`/`contractDetailsEnd`/`error` accumulating correlator.
- `FuturesFrontMonthSelector.cs` — pure front-month selection from candidate contracts.
- `IbConnectionOptions.cs` — `(Host, Port, ClientId)`.
- `IbConnection.cs` — transport primitive (socket + `EReader` pump + connect/disconnect).
- `IIbContractDetailsClient.cs` — seam interface.
- `IbConnectionContractDetailsClient.cs` — real impl over `IbConnection` + `IbWrapper` + `TimeProvider`.
- `IIbContractResolver.cs` — resolver interface.
- `IbContractResolver.cs` — caching impl over `IIbContractDetailsClient`.

**Tests** (`tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/`, namespace `…Tests.Live.InteractiveBrokers`):
- `IbSecTypeTests.cs`, `IbContractTests.cs`, `IbContractMappingTests.cs`, `IbContractTranslationTests.cs`, `IbWrapperTests.cs`, `FuturesFrontMonthSelectorTests.cs`, `IbContractResolverTests.cs`.
- `IbPaperGatewayConfig.cs` + `IbContractResolverPaperTests.cs` — gated integration (AAPL STK + GC FUT).

---

## Task 1: Vendor IBApi + wire into the solution

**Files:**
- Create: `src/AlgoTradeForge.IbApi/IBApi.csproj`
- Create (copied): `src/AlgoTradeForge.IbApi/*.cs`, `src/AlgoTradeForge.IbApi/protobuf/*.cs`
- Modify: `AlgoTradeForge.slnx`
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/AlgoTradeForge.LiveHost.Infrastructure.csproj`

**Interfaces:**
- Produces: a referenceable `IBApi` assembly (namespace `IBApi`: `EClientSocket`, `EReader`, `EReaderMonitorSignal`, `DefaultEWrapper`, `Contract`, `ContractDetails`).

- [ ] **Step 1: Copy the vendored source** (present on this machine at `poc/ib-connector/ibapi/`)

Run:
```powershell
New-Item -ItemType Directory -Force src/AlgoTradeForge.IbApi/protobuf | Out-Null
Copy-Item poc/ib-connector/ibapi/*.cs            src/AlgoTradeForge.IbApi/
Copy-Item poc/ib-connector/ibapi/protobuf/*.cs   src/AlgoTradeForge.IbApi/protobuf/
(Get-ChildItem src/AlgoTradeForge.IbApi/*.cs).Count   # expect ~68
```

- [ ] **Step 2: Create the vendored csproj**

`src/AlgoTradeForge.IbApi/IBApi.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!--
    Vendored Interactive Brokers TWS API C# source (version 10.45.01).
    Third-party, not ours to maintain: nullable OFF (predates NRT), implicit usings OFF
    (source carries explicit usings), warnings NOT errors (the root Directory.Build.props
    forces TreatWarningsAsErrors=true solution-wide; override it only here).
  -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>IBApi</AssemblyName>
    <RootNamespace>IBApi</RootNamespace>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <NoWarn>$(NoWarn);CS8981</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.29.5" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Register in the solution** — add to `AlgoTradeForge.slnx` alongside the other `src\` projects:
```xml
  <Project Path="src\AlgoTradeForge.IbApi\AlgoTradeForge.IbApi.csproj" />
```

- [ ] **Step 4: Reference from LiveHost.Infrastructure** — add to its `<ItemGroup>` of ProjectReferences:
```xml
    <ProjectReference Include="..\AlgoTradeForge.IbApi\AlgoTradeForge.IbApi.csproj" />
```

- [ ] **Step 5: Build the whole solution**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: build succeeds (vendored IBApi compiles nullable-off; no warnings leak into the rest of the solution).

- [ ] **Step 6: Commit** (controller)
```bash
git add src/AlgoTradeForge.IbApi AlgoTradeForge.slnx src/AlgoTradeForge.LiveHost.Infrastructure/AlgoTradeForge.LiveHost.Infrastructure.csproj
git commit -F - <<'EOF'
feat(livehost): vendor IBApi 10.45.01 and wire into the solution

Plan 1 Task 1. New AlgoTradeForge.IbApi (nullable-off, third-party TWS API
source) referenced by LiveHost.Infrastructure. Solution builds clean.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01QHhciYaRa2p148hh17h3X6
EOF
```

---

## Task 2: `IbSecType` + `IbContract` + `ResolvedIbContract`

**Files:**
- Create: `src/.../Live/InteractiveBrokers/IbSecType.cs`
- Create: `src/.../Live/InteractiveBrokers/IbSecTypeExtensions.cs`
- Create: `src/.../Live/InteractiveBrokers/IbContract.cs`
- Create: `src/.../Live/InteractiveBrokers/ResolvedIbContract.cs`
- Test: `tests/.../Live/InteractiveBrokers/IbSecTypeTests.cs`
- Test: `tests/.../Live/InteractiveBrokers/IbContractTests.cs`

**Interfaces:**
- Produces:
  - `enum IbSecType { Stk, Fut }`
  - `string ToIbString(this IbSecType)` (`Stk→"STK"`, `Fut→"FUT"`); `IbSecType FromIbString(string)`
  - `record IbContract(string Symbol, IbSecType SecType, string Exchange, string PrimaryExch, string Currency)`
  - `record ResolvedIbContract(IbContract Spec, int ConId, string LocalSymbol, string LastTradeDate)`

- [ ] **Step 1: Write the failing tests**

`IbSecTypeTests.cs`:
```csharp
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbSecTypeTests
{
    [Theory]
    [InlineData(IbSecType.Stk, "STK")]
    [InlineData(IbSecType.Fut, "FUT")]
    public void ToIbString_MapsEachMember(IbSecType type, string expected) =>
        Assert.Equal(expected, type.ToIbString());

    [Theory]
    [InlineData("STK", IbSecType.Stk)]
    [InlineData("FUT", IbSecType.Fut)]
    public void FromIbString_RoundTrips(string raw, IbSecType expected) =>
        Assert.Equal(expected, IbSecTypeExtensions.FromIbString(raw));

    [Fact]
    public void FromIbString_Unknown_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => IbSecTypeExtensions.FromIbString("OPT"));
}
```

`IbContractTests.cs`:
```csharp
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbContractTests
{
    [Fact]
    public void IbContract_ValueEquality_EnablesCacheKey()
    {
        var a = new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");
        var b = new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void IbContract_DifferingField_NotEqual()
    {
        var a = new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");
        var b = a with { Currency = "EUR" };
        Assert.NotEqual(a, b);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~InteractiveBrokers"`
Expected: FAIL (types not defined / compile error).

- [ ] **Step 3: Implement the types**

`IbSecType.cs`:
```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal enum IbSecType
{
    Stk,
    Fut,
}
```

`IbSecTypeExtensions.cs`:
```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal static class IbSecTypeExtensions
{
    public static string ToIbString(this IbSecType type) => type switch
    {
        IbSecType.Stk => "STK",
        IbSecType.Fut => "FUT",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static IbSecType FromIbString(string raw) => raw switch
    {
        "STK" => IbSecType.Stk,
        "FUT" => IbSecType.Fut,
        _ => throw new ArgumentOutOfRangeException(nameof(raw), raw, "Unsupported IB security type."),
    };
}
```

`IbContract.cs`:
```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Configured tier: round-trips through session config; value equality makes it the resolver cache key.
// Exchange is the IB routing destination (STK -> "SMART"; FUT -> the futures exchange, e.g. "COMEX").
// PrimaryExch is the listing exchange for stocks (e.g. "NASDAQ") and empty for futures.
internal sealed record IbContract(
    string Symbol,
    IbSecType SecType,
    string Exchange,
    string PrimaryExch,
    string Currency);
```

`ResolvedIbContract.cs`:
```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Resolved tier: the configured contract plus the runtime conId, localSymbol, and (for futures) the chosen
// front-month expiry from reqContractDetails. LastTradeDate is empty for equities.
internal sealed record ResolvedIbContract(
    IbContract Spec,
    int ConId,
    string LocalSymbol,
    string LastTradeDate);
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~InteractiveBrokers"`
Expected: PASS.

- [ ] **Step 5: Commit** (controller)
```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers
git commit -F - <<'EOF'
feat(livehost): IbSecType + two-tier IbContract model

Plan 1 Task 2. Configured-tier IbContract (value-equality cache key),
resolved-tier ResolvedIbContract (conId/localSymbol/lastTradeDate),
IbSecType <-> IB string mapping.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01QHhciYaRa2p148hh17h3X6
EOF
```

---

## Task 3: Polymorphic `Asset` ↔ `IbContract` mapper

**Files:**
- Create: `src/.../Live/InteractiveBrokers/IbContractMapping.cs`
- Test: `tests/.../Live/InteractiveBrokers/IbContractMappingTests.cs`

**Interfaces:**
- Consumes: `IbContract`, `ResolvedIbContract`, `IbSecType` (Task 2); Domain `Asset`/`EquityAsset`/`FutureAsset`/`CryptoAsset`/`CryptoPerpetualAsset`.
- Produces: `IbContract ToIbContract(this Asset)`; `Asset ToAsset(this ResolvedIbContract)`.

- [ ] **Step 1: Write the failing tests**

`IbContractMappingTests.cs`:
```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbContractMappingTests
{
    [Fact]
    public void ToIbContract_Equity_RoutesSmartWithPrimaryExch()
    {
        var aapl = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };

        var c = aapl.ToIbContract();

        Assert.Equal("AAPL", c.Symbol);
        Assert.Equal(IbSecType.Stk, c.SecType);
        Assert.Equal("SMART", c.Exchange);     // routing default
        Assert.Equal("NASDAQ", c.PrimaryExch); // listing <- Asset.Exchange
        Assert.Equal("USD", c.Currency);       // default; multi-currency deferred
    }

    [Fact]
    public void ToIbContract_Future_RoutesDirectExchangeNoPrimary()
    {
        var gold = FutureAsset.Create("GC", "COMEX", multiplier: 100m, tickSize: 0.1m);

        var c = gold.ToIbContract();

        Assert.Equal("GC", c.Symbol);
        Assert.Equal(IbSecType.Fut, c.SecType);
        Assert.Equal("COMEX", c.Exchange);  // futures route to the direct exchange <- Asset.Exchange
        Assert.Equal("", c.PrimaryExch);    // futures have no primary-listing exchange
        Assert.Equal("USD", c.Currency);
    }

    [Fact]
    public void ToIbContract_Crypto_NotSupported() =>
        Assert.Throws<NotSupportedException>(() =>
            CryptoAsset.Create("BTCUSDT", "Binance", decimalDigits: 2).ToIbContract());

    [Fact]
    public void ToIbContract_CryptoPerpetual_NotSupported() =>
        Assert.Throws<NotSupportedException>(() =>
            CryptoPerpetualAsset.Create("BTCUSDT", "Binance", decimalDigits: 2).ToIbContract());

    [Fact]
    public void ToAsset_Stk_BuildsEquityAsset()
    {
        var resolved = new ResolvedIbContract(
            new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD"),
            ConId: 265598, LocalSymbol: "AAPL", LastTradeDate: "");

        var asset = resolved.ToAsset();

        var equity = Assert.IsType<EquityAsset>(asset);
        Assert.Equal("AAPL", equity.Name);
        Assert.Equal("NASDAQ", equity.Exchange);
    }

    [Fact]
    public void ToAsset_Fut_NotSupported_PendingEnrichment()
    {
        var resolved = new ResolvedIbContract(
            new IbContract("GC", IbSecType.Fut, "COMEX", "", "USD"),
            ConId: 1, LocalSymbol: "GCZ6", LastTradeDate: "20261229");
        Assert.Throws<NotSupportedException>(() => resolved.ToAsset());
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~IbContractMappingTests"`
Expected: FAIL (extension methods not defined).

- [ ] **Step 3: Implement the mapper** (mirrors `SettlementCalculatorExtensions.GetSettlementCalculator`)

`IbContractMapping.cs`:
```csharp
using AlgoTradeForge.Domain;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Polymorphic Asset <-> IbContract mapping: the GetSettlementCalculator(this Asset) idiom applied to
// instrument identity. Domain stays venue-neutral; all IB vocabulary lives here in the venue slice.
// Equity routes via SMART with a primary-listing exchange; futures route to their direct exchange with no
// primary and are resolved to a front-month conId later by the resolver.
internal static class IbContractMapping
{
    private const string SmartRouting = "SMART";
    private const string DefaultCurrency = "USD";

    public static IbContract ToIbContract(this Asset asset) => asset switch
    {
        EquityAsset => new IbContract(asset.Name, IbSecType.Stk, SmartRouting, asset.Exchange, DefaultCurrency),
        FutureAsset => new IbContract(asset.Name, IbSecType.Fut, asset.Exchange, PrimaryExch: "", DefaultCurrency),
        CryptoAsset => throw new NotSupportedException(
            "IB crypto routes via PAXOS and differs from a Binance-spot CryptoAsset — deferred to a later plan."),
        CryptoPerpetualAsset => throw new NotSupportedException(
            "Interactive Brokers has no crypto perpetual contracts."),
        _ => throw new ArgumentOutOfRangeException(
            nameof(asset), asset.GetType().Name, "Unsupported asset type for IB contract mapping."),
    };

    // Reverse map (for Plan 2/4 reconciliation of IB position/order pushback). Equity needs no enrichment;
    // futures reconstruction needs contractDetails multiplier/minTick (deferred — spec open point #3).
    public static Asset ToAsset(this ResolvedIbContract resolved) => resolved.Spec.SecType switch
    {
        IbSecType.Stk => new EquityAsset { Name = resolved.Spec.Symbol, Exchange = resolved.Spec.PrimaryExch },
        IbSecType.Fut => throw new NotSupportedException(
            "FutureAsset reconstruction needs contractDetails multiplier/minTick — deferred to Plan 2/4."),
        _ => throw new ArgumentOutOfRangeException(
            nameof(resolved), resolved.Spec.SecType, "Unsupported SecType for asset mapping."),
    };
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~IbContractMappingTests"`
Expected: PASS.

- [ ] **Step 5: Commit** (controller)
```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers
git commit -F - <<'EOF'
feat(livehost): polymorphic Asset <-> IbContract mapper (equity + futures)

Plan 1 Task 3. ToIbContract/ToAsset dispatch on the Asset hierarchy
(GetSettlementCalculator idiom); EquityAsset->STK (SMART+primaryExch) and
FutureAsset->FUT (direct exchange) implemented, crypto kinds fenced. Reverse
map is STK-only; FUT reverse deferred to Plan 2/4 enrichment.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01QHhciYaRa2p148hh17h3X6
EOF
```

---

## Task 4: `IbContract` → `IBApi.Contract` translation

**Files:**
- Create: `src/.../Live/InteractiveBrokers/IbContractTranslation.cs`
- Modify: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/AlgoTradeForge.LiveHost.Infrastructure.Tests.csproj` (add IBApi ProjectReference so tests can construct `IBApi.Contract`)
- Test: `tests/.../Live/InteractiveBrokers/IbContractTranslationTests.cs`

**Interfaces:**
- Consumes: `IbContract`, `IbSecType.ToIbString()` (Task 2); `IBApi.Contract` (Task 1).
- Produces: `IBApi.Contract ToIbApiContract(this IbContract)`. (No expiry is sent: a futures request is intentionally expiry-less so IB returns every listed month for front-month selection.)

- [ ] **Step 1: Add IBApi ProjectReference to the test project** — add to the `<ItemGroup>` of ProjectReferences in `AlgoTradeForge.LiveHost.Infrastructure.Tests.csproj`:
```xml
    <ProjectReference Include="..\..\src\AlgoTradeForge.IbApi\AlgoTradeForge.IbApi.csproj" />
```

- [ ] **Step 2: Write the failing test**

`IbContractTranslationTests.cs`:
```csharp
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbContractTranslationTests
{
    [Fact]
    public void ToIbApiContract_Equity_MapsEveryField()
    {
        var spec = new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");

        var ib = spec.ToIbApiContract();

        Assert.Equal("AAPL", ib.Symbol);
        Assert.Equal("STK", ib.SecType);
        Assert.Equal("SMART", ib.Exchange);
        Assert.Equal("NASDAQ", ib.PrimaryExch);
        Assert.Equal("USD", ib.Currency);
        Assert.Equal(0, ib.ConId); // unresolved until reqContractDetails
    }

    [Fact]
    public void ToIbApiContract_Future_SendsNoExpiry()
    {
        var spec = new IbContract("GC", IbSecType.Fut, "COMEX", "", "USD");

        var ib = spec.ToIbApiContract();

        Assert.Equal("GC", ib.Symbol);
        Assert.Equal("FUT", ib.SecType);
        Assert.Equal("COMEX", ib.Exchange);
        // expiry-less so IB returns all listed months for front-month selection
        Assert.True(string.IsNullOrEmpty(ib.LastTradeDateOrContractMonth));
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~IbContractTranslationTests"`
Expected: FAIL (method not defined).

- [ ] **Step 4: Implement the translation**

`IbContractTranslation.cs`:
```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Translates the venue-neutral configured IbContract into the vendored IBApi wire type for reqContractDetails
// / placeOrder. Kept separate from the Asset<->IbContract mapper: this is the venue-DTO boundary (the IBApi
// reference stops here), mirroring BinanceAggTrade at the parser boundary. Futures are sent expiry-less so a
// single reqContractDetails returns every listed month.
internal static class IbContractTranslation
{
    public static IBApi.Contract ToIbApiContract(this IbContract spec) => new()
    {
        Symbol = spec.Symbol,
        SecType = spec.SecType.ToIbString(),
        Exchange = spec.Exchange,
        PrimaryExch = spec.PrimaryExch,
        Currency = spec.Currency,
    };
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~IbContractTranslationTests"`
Expected: PASS.

- [ ] **Step 6: Commit** (controller)
```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers tests/AlgoTradeForge.LiveHost.Infrastructure.Tests
git commit -F - <<'EOF'
feat(livehost): IbContract -> IBApi.Contract translation

Plan 1 Task 4. Venue-DTO boundary mapping the configured IbContract to the
vendored IBApi wire type; the IBApi reference stops at this seam. Futures sent
expiry-less for front-month enumeration.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01QHhciYaRa2p148hh17h3X6
EOF
```

---

## Task 5: `IbWrapper` accumulating correlator + `IbContractDetailsResult` + `IbRequestException`

**Files:**
- Create: `src/.../Live/InteractiveBrokers/IbContractDetailsResult.cs`
- Create: `src/.../Live/InteractiveBrokers/IbRequestException.cs`
- Create: `src/.../Live/InteractiveBrokers/IbWrapper.cs`
- Test: `tests/.../Live/InteractiveBrokers/IbWrapperTests.cs`

**Interfaces:**
- Consumes: `IBApi.DefaultEWrapper`, `IBApi.Contract`, `IBApi.ContractDetails` (Task 1).
- Produces:
  - `record IbContractDetailsResult(int ConId, string LocalSymbol, string LastTradeDate)`
  - `class IbRequestException(int errorCode, string errorMessage) : Exception` with `int ErrorCode`, `string ErrorMessage`.
  - `class IbWrapper : DefaultEWrapper` with `Task<int> NextValidId`, `Task<IReadOnlyList<IbContractDetailsResult>> AwaitContractDetails(int reqId)`.
- **Ordering contract:** a consumer MUST call `AwaitContractDetails(reqId)` *before* issuing `reqContractDetails(reqId, …)`. The correlator accumulates each `contractDetails(reqId, …)` and completes the awaiter on `contractDetailsEnd(reqId)`; an `error(reqId, …)` with `id >= 0` faults it. Unsolicited callbacks (no registered awaiter) are ignored.

- [ ] **Step 1: Write the failing tests**

`IbWrapperTests.cs`:
```csharp
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using IBApi;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbWrapperTests
{
    private static ContractDetails Details(int conId, string localSymbol, string expiry = "") =>
        new() { Contract = new Contract { ConId = conId, LocalSymbol = localSymbol, LastTradeDateOrContractMonth = expiry } };

    [Fact]
    public async Task ContractDetailsEnd_CompletesWithAllAccumulated()
    {
        var w = new IbWrapper();
        var awaiter = w.AwaitContractDetails(1);

        w.contractDetails(1, Details(1, "GCZ6", "20261229"));
        w.contractDetails(1, Details(2, "GCG7", "20270226"));
        w.contractDetailsEnd(1);

        var results = await awaiter;
        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].ConId);
        Assert.Equal("20261229", results[0].LastTradeDate);
        Assert.Equal(2, results[1].ConId);
    }

    [Fact]
    public async Task SingleStk_CompletesWithOne()
    {
        var w = new IbWrapper();
        var awaiter = w.AwaitContractDetails(3);

        w.contractDetails(3, Details(265598, "AAPL"));
        w.contractDetailsEnd(3);

        var results = await awaiter;
        var only = Assert.Single(results);
        Assert.Equal(265598, only.ConId);
        Assert.Equal("AAPL", only.LocalSymbol);
    }

    [Fact]
    public async Task Error_OnRequestId_FaultsAwaiter()
    {
        var w = new IbWrapper();
        var awaiter = w.AwaitContractDetails(7);

        w.error(7, 0L, 200, "No security definition has been found", "");

        var ex = await Assert.ThrowsAsync<IbRequestException>(async () => await awaiter);
        Assert.Equal(200, ex.ErrorCode);
    }

    [Fact]
    public void Error_ConnectivityNotice_IgnoresMinusOne()
    {
        var w = new IbWrapper();
        // id == -1 is a data-farm/connectivity notice, must not fault any awaiter.
        w.error(-1, 0L, 2104, "Market data farm connection is OK", "");
    }

    [Fact]
    public async Task NextValidId_CompletesTask()
    {
        var w = new IbWrapper();
        w.nextValidId(42);
        Assert.Equal(42, await w.NextValidId);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~IbWrapperTests"`
Expected: FAIL (types not defined).

- [ ] **Step 3: Implement the types**

`IbContractDetailsResult.cs`:
```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal sealed record IbContractDetailsResult(int ConId, string LocalSymbol, string LastTradeDate);
```

`IbRequestException.cs`:
```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal sealed class IbRequestException(int errorCode, string errorMessage)
    : Exception($"IB request failed (code {errorCode}): {errorMessage}")
{
    public int ErrorCode { get; } = errorCode;
    public string ErrorMessage { get; } = errorMessage;
}
```

`IbWrapper.cs`:
```csharp
using System.Collections.Concurrent;
using IBApi;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Derives from DefaultEWrapper so only the callbacks Plan 1 exercises are overridden; every other EWrapper
// member (incl. 10.45 ProtoBuf variants) inherits an empty body. Accumulates contractDetails per reqId and
// completes the awaiter on contractDetailsEnd (a single reqContractDetails returns many months for a futures
// family). Callbacks fire on the single EReader pump thread, so per-reqId accumulation is not concurrent.
// Plan 3/4 grow this with tick / order / fill callbacks.
internal sealed class IbWrapper : DefaultEWrapper
{
    private sealed class Pending
    {
        public List<IbContractDetailsResult> Items { get; } = [];
        public TaskCompletionSource<IReadOnlyList<IbContractDetailsResult>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly TaskCompletionSource<int> _nextValidId =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, Pending> _byReq = new();

    public Task<int> NextValidId => _nextValidId.Task;

    public Task<IReadOnlyList<IbContractDetailsResult>> AwaitContractDetails(int reqId) =>
        _byReq.GetOrAdd(reqId, _ => new Pending()).Completion.Task;

    public override void nextValidId(int orderId) => _nextValidId.TrySetResult(orderId);

    public override void contractDetails(int reqId, ContractDetails contractDetails)
    {
        if (_byReq.TryGetValue(reqId, out var pending))
            pending.Items.Add(new IbContractDetailsResult(
                contractDetails.Contract.ConId,
                contractDetails.Contract.LocalSymbol,
                contractDetails.Contract.LastTradeDateOrContractMonth ?? ""));
    }

    public override void contractDetailsEnd(int reqId)
    {
        if (_byReq.TryGetValue(reqId, out var pending))
            pending.Completion.TrySetResult(pending.Items.ToArray());
    }

    public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        // Connectivity / data-farm notices arrive with id == -1; never correlate those to a request.
        if (id >= 0 && _byReq.TryGetValue(id, out var pending))
            pending.Completion.TrySetException(new IbRequestException(errorCode, errorMsg));
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~IbWrapperTests"`
Expected: PASS.

- [ ] **Step 5: Commit** (controller)
```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers
git commit -F - <<'EOF'
feat(livehost): IbWrapper accumulating contractDetails correlator

Plan 1 Task 5. DefaultEWrapper-derived correlator accumulating contractDetails
per reqId, completing on contractDetailsEnd (futures families return many
months), faulting on a matching error (id>=0), exposing nextValidId.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01QHhciYaRa2p148hh17h3X6
EOF
```

---

## Task 6: `FuturesFrontMonthSelector`

**Files:**
- Create: `src/.../Live/InteractiveBrokers/FuturesFrontMonthSelector.cs`
- Test: `tests/.../Live/InteractiveBrokers/FuturesFrontMonthSelectorTests.cs`

**Interfaces:**
- Consumes: `IbContractDetailsResult` (Task 5).
- Produces: `IbContractDetailsResult SelectFrontMonth(IReadOnlyList<IbContractDetailsResult> candidates, DateOnly today)` — the nearest non-expired contract (min `LastTradeDate >= today`). Parses IB `LastTradeDateOrContractMonth` (`yyyymmdd` or `yyyymm`). Roll timing (rolling out before expiry) is a trading concern deferred to Plan 3/4.

- [ ] **Step 1: Write the failing tests**

`FuturesFrontMonthSelectorTests.cs`:
```csharp
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class FuturesFrontMonthSelectorTests
{
    private static IbContractDetailsResult C(int conId, string expiry) => new(conId, $"GC{conId}", expiry);

    [Fact]
    public void SelectFrontMonth_PicksNearestNonExpired()
    {
        var today = new DateOnly(2026, 6, 26);
        var candidates = new[] { C(3, "20270226"), C(1, "20261229"), C(2, "20270127") };

        var chosen = FuturesFrontMonthSelector.SelectFrontMonth(candidates, today);

        Assert.Equal(1, chosen.ConId);
        Assert.Equal("20261229", chosen.LastTradeDate);
    }

    [Fact]
    public void SelectFrontMonth_SkipsExpired()
    {
        var today = new DateOnly(2026, 6, 26);
        var candidates = new[] { C(1, "20260130"), C(2, "20260828") };

        var chosen = FuturesFrontMonthSelector.SelectFrontMonth(candidates, today);

        Assert.Equal(2, chosen.ConId);
    }

    [Fact]
    public void SelectFrontMonth_AcceptsYearMonthFormat()
    {
        var today = new DateOnly(2026, 6, 26);
        var candidates = new[] { C(1, "202612"), C(2, "202703") };

        var chosen = FuturesFrontMonthSelector.SelectFrontMonth(candidates, today);

        Assert.Equal(1, chosen.ConId);
    }

    [Fact]
    public void SelectFrontMonth_AllExpired_Throws()
    {
        var today = new DateOnly(2026, 6, 26);
        var candidates = new[] { C(1, "20260130") };
        Assert.Throws<InvalidOperationException>(() => FuturesFrontMonthSelector.SelectFrontMonth(candidates, today));
    }

    [Fact]
    public void SelectFrontMonth_Empty_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            FuturesFrontMonthSelector.SelectFrontMonth([], new DateOnly(2026, 6, 26)));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~FuturesFrontMonthSelectorTests"`
Expected: FAIL (type not defined).

- [ ] **Step 3: Implement the selector**

`FuturesFrontMonthSelector.cs`:
```csharp
using System.Globalization;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Picks the front-month futures contract: the nearest expiry on or after today. Roll timing (switching to the
// next month a few days before expiry) is a trading concern deferred to Plan 3/4.
internal static class FuturesFrontMonthSelector
{
    public static IbContractDetailsResult SelectFrontMonth(
        IReadOnlyList<IbContractDetailsResult> candidates, DateOnly today)
    {
        if (candidates.Count == 0)
            throw new InvalidOperationException("No contract details returned for futures resolution.");

        IbContractDetailsResult? best = null;
        var bestDate = DateOnly.MaxValue;
        foreach (var candidate in candidates)
        {
            var expiry = ParseExpiry(candidate.LastTradeDate);
            if (expiry < today || expiry >= bestDate) continue;
            best = candidate;
            bestDate = expiry;
        }

        return best ?? throw new InvalidOperationException("All returned futures contracts are expired.");
    }

    // IB LastTradeDateOrContractMonth is "yyyymmdd" or "yyyymm".
    private static DateOnly ParseExpiry(string raw) => raw.Length switch
    {
        8 => DateOnly.ParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture),
        6 => DateOnly.ParseExact(raw + "01", "yyyyMMdd", CultureInfo.InvariantCulture),
        _ => throw new FormatException($"Unrecognized IB expiry format: '{raw}'."),
    };
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~FuturesFrontMonthSelectorTests"`
Expected: PASS.

- [ ] **Step 5: Commit** (controller)
```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers
git commit -F - <<'EOF'
feat(livehost): futures front-month selector

Plan 1 Task 6. Pure selection of the nearest non-expired futures contract from
reqContractDetails candidates; parses yyyymmdd / yyyymm. Roll timing deferred.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01QHhciYaRa2p148hh17h3X6
EOF
```

---

## Task 7: `IIbContractDetailsClient` seam + caching `IbContractResolver`

**Files:**
- Create: `src/.../Live/InteractiveBrokers/IIbContractDetailsClient.cs`
- Create: `src/.../Live/InteractiveBrokers/IIbContractResolver.cs`
- Create: `src/.../Live/InteractiveBrokers/IbContractResolver.cs`
- Test: `tests/.../Live/InteractiveBrokers/IbContractResolverTests.cs`

**Interfaces:**
- Consumes: `IbContract`, `ResolvedIbContract` (Task 2).
- Produces:
  - `interface IIbContractDetailsClient { Task<ResolvedIbContract> FetchContractDetails(IbContract spec, CancellationToken ct = default); }`
  - `interface IIbContractResolver { Task<ResolvedIbContract> Resolve(IbContract spec, CancellationToken ct = default); }`
  - `class IbContractResolver(IIbContractDetailsClient client) : IIbContractResolver` — caches successful results by `IbContract` value equality.

- [ ] **Step 1: Write the failing tests**

`IbContractResolverTests.cs`:
```csharp
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbContractResolverTests
{
    private static IbContract Spec(string symbol = "AAPL") =>
        new(symbol, IbSecType.Stk, "SMART", "NASDAQ", "USD");

    [Fact]
    public async Task Resolve_CacheMiss_FetchesAndReturns()
    {
        var spec = Spec();
        var client = Substitute.For<IIbContractDetailsClient>();
        client.FetchContractDetails(spec, Arg.Any<CancellationToken>())
            .Returns(new ResolvedIbContract(spec, 265598, "AAPL", ""));
        var resolver = new IbContractResolver(client);

        var resolved = await resolver.Resolve(spec);

        Assert.Equal(265598, resolved.ConId);
        await client.Received(1).FetchContractDetails(spec, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_CacheHit_DoesNotRefetch()
    {
        var spec = Spec();
        var client = Substitute.For<IIbContractDetailsClient>();
        client.FetchContractDetails(spec, Arg.Any<CancellationToken>())
            .Returns(new ResolvedIbContract(spec, 265598, "AAPL", ""));
        var resolver = new IbContractResolver(client);

        var first = await resolver.Resolve(spec);
        var second = await resolver.Resolve(spec);

        Assert.Same(first, second);
        await client.Received(1).FetchContractDetails(spec, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_DistinctSpecs_FetchedIndependently()
    {
        var a = Spec("AAPL");
        var b = Spec("MSFT");
        var client = Substitute.For<IIbContractDetailsClient>();
        client.FetchContractDetails(a, Arg.Any<CancellationToken>()).Returns(new ResolvedIbContract(a, 1, "AAPL", ""));
        client.FetchContractDetails(b, Arg.Any<CancellationToken>()).Returns(new ResolvedIbContract(b, 2, "MSFT", ""));
        var resolver = new IbContractResolver(client);

        Assert.Equal(1, (await resolver.Resolve(a)).ConId);
        Assert.Equal(2, (await resolver.Resolve(b)).ConId);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~IbContractResolverTests"`
Expected: FAIL (types not defined).

- [ ] **Step 3: Implement the seam + resolver**

`IIbContractDetailsClient.cs`:
```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The socket round-trip seam: translate a configured contract, issue reqContractDetails, select a single
// contract (one for STK, front-month for FUT). Faked in unit tests; the real impl
// (IbConnectionContractDetailsClient) drives a live IbConnection.
internal interface IIbContractDetailsClient
{
    Task<ResolvedIbContract> FetchContractDetails(IbContract spec, CancellationToken ct = default);
}
```

`IIbContractResolver.cs`:
```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal interface IIbContractResolver
{
    Task<ResolvedIbContract> Resolve(IbContract spec, CancellationToken ct = default);
}
```

`IbContractResolver.cs`:
```csharp
using System.Collections.Concurrent;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Caches successful resolutions by configured-contract value equality. Resolution happens once per instrument
// at startup and reqContractDetails is idempotent, so a rare concurrent first-miss double-fetch is acceptable
// (last-writer-wins); we deliberately do not cache faulted tasks.
internal sealed class IbContractResolver(IIbContractDetailsClient client) : IIbContractResolver
{
    private readonly ConcurrentDictionary<IbContract, ResolvedIbContract> _cache = new();

    public async Task<ResolvedIbContract> Resolve(IbContract spec, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(spec, out var cached))
            return cached;

        var resolved = await client.FetchContractDetails(spec, ct);
        _cache[spec] = resolved;
        return resolved;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~IbContractResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit** (controller)
```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers
git commit -F - <<'EOF'
feat(livehost): caching IbContractResolver over IIbContractDetailsClient

Plan 1 Task 7. Resolver caches successful resolutions by configured-contract
value equality; socket round-trip abstracted behind IIbContractDetailsClient
(faked in unit tests).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01QHhciYaRa2p148hh17h3X6
EOF
```

---

## Task 8: `IbConnection` transport + real client + gated paper integration tests

**Files:**
- Create: `src/.../Live/InteractiveBrokers/IbConnectionOptions.cs`
- Create: `src/.../Live/InteractiveBrokers/IbConnection.cs`
- Create: `src/.../Live/InteractiveBrokers/IbConnectionContractDetailsClient.cs`
- Test: `tests/.../Live/InteractiveBrokers/IbPaperGatewayConfig.cs`
- Test: `tests/.../Live/InteractiveBrokers/IbContractResolverPaperTests.cs`

**Interfaces:**
- Consumes: `IbWrapper` (Task 5), `FuturesFrontMonthSelector` (Task 6), `IbContract.ToIbApiContract()` (Task 4), `IIbContractDetailsClient` (Task 7), `IBApi.EClientSocket`/`EReader`/`EReaderMonitorSignal` (Task 1).
- Produces:
  - `record IbConnectionOptions(string Host, int Port, int ClientId)`
  - `class IbConnection : IAsyncDisposable` with `EClientSocket Client { get; }`, `Task Connect(int maxAttempts = 90, int retryDelayMs = 2000, CancellationToken ct = default)`, `void Disconnect()`.
  - `class IbConnectionContractDetailsClient(IbConnection connection, IbWrapper wrapper, TimeProvider timeProvider) : IIbContractDetailsClient`.

- [ ] **Step 1: Implement the transport primitive** (hardened from the proven POC `IbConnection`)

`IbConnectionOptions.cs`:
```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal sealed record IbConnectionOptions(string Host, int Port, int ClientId);
```

`IbConnection.cs`:
```csharp
using IBApi;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The single IB transport: owns one EClientSocket + EReader pump thread. Plan 1 uses it for
// reqContractDetails; Plan 3 grows IbSession around this exact primitive (tick streaming + shared order
// socket). The wrapper is supplied so the data/order planes can share one callback sink.
internal sealed class IbConnection(IbWrapper wrapper, IbConnectionOptions options) : IAsyncDisposable
{
    private readonly EReaderMonitorSignal _signal = new();
    private EClientSocket? _client;
    private Thread? _readerThread;

    public EClientSocket Client => _client ?? throw new InvalidOperationException("IB connection is not established.");

    // 90 attempts (~3 min): gateway cold start (IBC login + API socket bind) routinely exceeds 60s, and the
    // first socket is often reset once by the 10141 paper-trading disclaimer before the API binds.
    public async Task Connect(int maxAttempts = 90, int retryDelayMs = 2000, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _client = new EClientSocket(wrapper, _signal);
            try
            {
                _client.eConnect(options.Host, options.Port, options.ClientId);
                if (_client.IsConnected())
                {
                    StartReaderPump(_client);
                    await wrapper.NextValidId.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    return;
                }
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                // transient gateway-cold-start failure; retry below
            }
            await Task.Delay(retryDelayMs, ct);
        }
        throw new TimeoutException($"Could not connect to IB Gateway at {options.Host}:{options.Port}.");
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
            _client.eDisconnect();
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }
}
```

`IbConnectionContractDetailsClient.cs`:
```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Real IIbContractDetailsClient: allocates a reqId, registers the awaiter BEFORE issuing the request
// (the IbWrapper ordering contract), reqContractDetails over the shared socket, then selects one contract:
// exactly one for STK, the front month for FUT.
internal sealed class IbConnectionContractDetailsClient(
    IbConnection connection, IbWrapper wrapper, TimeProvider timeProvider) : IIbContractDetailsClient
{
    private int _reqId;

    public async Task<ResolvedIbContract> FetchContractDetails(IbContract spec, CancellationToken ct = default)
    {
        var reqId = Interlocked.Increment(ref _reqId);
        var awaiter = wrapper.AwaitContractDetails(reqId);
        connection.Client.reqContractDetails(reqId, spec.ToIbApiContract());
        var details = await awaiter.WaitAsync(TimeSpan.FromSeconds(15), ct);
        var chosen = Select(spec, details);
        return new ResolvedIbContract(spec, chosen.ConId, chosen.LocalSymbol, chosen.LastTradeDate);
    }

    private IbContractDetailsResult Select(IbContract spec, IReadOnlyList<IbContractDetailsResult> details) =>
        spec.SecType switch
        {
            IbSecType.Fut => FuturesFrontMonthSelector.SelectFrontMonth(
                details, DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime)),
            IbSecType.Stk => details.Count == 1
                ? details[0]
                : throw new InvalidOperationException(
                    $"Expected exactly one STK contract for '{spec.Symbol}', got {details.Count}."),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.SecType, null),
        };
}
```

- [ ] **Step 2: Add the gating helper**

`IbPaperGatewayConfig.cs`:
```csharp
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

// Gates the live paper integration tests. Configure via env vars to run locally against the gnzsnz
// ib-gateway compose stack; absent => the tests are skipped (CI has no gateway).
internal static class IbPaperGatewayConfig
{
    public static string? Host => Environment.GetEnvironmentVariable("IB_PAPER_HOST");
    public static int Port => int.TryParse(Environment.GetEnvironmentVariable("IB_PAPER_PORT"), out var p) ? p : 4004;
    public static int ClientId =>
        int.TryParse(Environment.GetEnvironmentVariable("IB_PAPER_CLIENT_ID"), out var c) ? c : 11;

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    public static IbConnectionOptions Options => new(Host!, Port, ClientId);

    public const string SkipReason =
        "IB paper gateway not configured. Start the gnzsnz ib-gateway stack and set IB_PAPER_HOST " +
        "(and optionally IB_PAPER_PORT=4004, IB_PAPER_CLIENT_ID) to run these integration tests.";
}
```

- [ ] **Step 3: Write the gated integration tests** (STK + FUT)

`IbContractResolverPaperTests.cs`:
```csharp
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

[Trait("Category", "IbPaper")]
public sealed class IbContractResolverPaperTests
{
    private static async Task<(IbConnection conn, IbContractResolver resolver)> ConnectAsync()
    {
        var wrapper = new IbWrapper();
        var conn = new IbConnection(wrapper, IbPaperGatewayConfig.Options);
        await conn.Connect();
        var client = new IbConnectionContractDetailsClient(conn, wrapper, TimeProvider.System);
        return (conn, new IbContractResolver(client));
    }

    [Fact]
    public async Task Resolve_AaplStk_ReturnsConId()
    {
        if (!IbPaperGatewayConfig.IsConfigured) Assert.Skip(IbPaperGatewayConfig.SkipReason);

        var (conn, resolver) = await ConnectAsync();
        await using var _ = conn;

        var resolved = await resolver.Resolve(new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD"));

        Assert.True(resolved.ConId > 0);
        Assert.Equal("AAPL", resolved.LocalSymbol);
    }

    [Fact]
    public async Task Resolve_GoldFuture_ReturnsFrontMonthConId()
    {
        if (!IbPaperGatewayConfig.IsConfigured) Assert.Skip(IbPaperGatewayConfig.SkipReason);

        var (conn, resolver) = await ConnectAsync();
        await using var _ = conn;

        var resolved = await resolver.Resolve(new IbContract("GC", IbSecType.Fut, "COMEX", "", "USD"));

        Assert.True(resolved.ConId > 0);
        Assert.False(string.IsNullOrEmpty(resolved.LastTradeDate)); // a concrete front-month expiry
    }
}
```

- [ ] **Step 4: Build, then run the non-gated suite (integration tests skip)**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~InteractiveBrokers"`
Expected: all unit tests PASS; both `IbPaper` tests report **Skipped** (`IB_PAPER_HOST` unset).

- [ ] **Step 5 (optional, local only): run the integration tests against a live gateway**

With the gnzsnz `ib-gateway` paper stack up:
```bash
IB_PAPER_HOST=127.0.0.1 IB_PAPER_PORT=4004 dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "Category=IbPaper"
```
Expected: both tests PASS (AAPL conId > 0; GC front-month conId > 0 with a concrete expiry). Contract resolution needs no market-data entitlement, so this works off-hours too.

- [ ] **Step 6: Commit** (controller)
```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers
git commit -F - <<'EOF'
feat(livehost): IbConnection transport + real conId resolver client

Plan 1 Task 8. Long-term IB transport primitive (socket + EReader pump,
hardened from the POC), real IIbContractDetailsClient over reqContractDetails
with STK/FUT front-month selection, and gated paper integration tests
resolving AAPL + GC -> conId.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01QHhciYaRa2p148hh17h3X6
EOF
```

---

## Final verification (after all tasks)

- [ ] Full solution builds: `dotnet build AlgoTradeForge.slnx` — clean.
- [ ] LiveHost.Infrastructure tests pass: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/` — all green, two `IbPaper` tests Skipped.
- [ ] Domain untouched: `git diff --stat main -- src/AlgoTradeForge.Domain` shows no changes.

## Self-Review (against the spec)

**Spec coverage:** vendored IBApi (Task 1) ✓; long-term transport primitive (Task 8) ✓; two-tier `IbContract` model (Task 2) ✓; polymorphic `GetSettlementCalculator`-style mapper (Task 3) ✓; real `IbContractResolver` + cache (Tasks 7–8) ✓; gated paper integration test resolving `{AAPL,STK,SMART,USD}`→conId (Task 8) ✓; commit-vendored-source decision (Task 1) ✓; Domain zero-vocabulary/zero-ProjectRefs (final verification) ✓.

**Refinements beyond the spec (owner-directed):** futures support added — `FutureAsset → FUT` forward mapping (Task 3) + front-month auto-resolution (Tasks 5, 6, 8) handled **without a Domain expiry field** (the resolver enumerates months and selects). The POC's "complete on first contractDetails" was corrected to accumulate-until-`contractDetailsEnd` (Task 5), required for futures. `CryptoAsset`/`CryptoPerpetualAsset` fenced; options + single-stock-futures deferred (nice-to-have, no Domain type). Reverse `FUT → FutureAsset` deferred to Plan 2/4 enrichment (spec open point #3). Multi-currency (open point #1) and connector-selection axis (open point #2) remain deferred.

**Placeholder scan:** none — every code/test step contains complete content.

**Type consistency:** `IbContract`/`ResolvedIbContract` (4 fields incl. `LastTradeDate`)/`IbSecType` (`Stk`,`Fut`)/`IbContractDetailsResult` (3 fields) field names and the method names `ToIbContract`/`ToAsset`/`ToIbApiContract`/`AwaitContractDetails`/`FetchContractDetails`/`SelectFrontMonth`/`Resolve`/`Connect` are used identically across tasks. `AwaitContractDetails` returns `Task<IReadOnlyList<IbContractDetailsResult>>` in Tasks 5 and 8. The `IbWrapper` ordering contract (register awaiter before request) is honored by `IbConnectionContractDetailsClient` (Task 8).
