# AlgoTradeForge.IbApi — vendored TWS API (EXTERNAL CODE)

**Third-party, not ours to maintain.** This folder is the **Interactive Brokers
TWS API 10.45.01** C# client (client `.cs` + `protobuf/`), vendored in-tree as a
backup of the exact stable version LiveHost@ib is built against. It compiles as a
nullable-off library (`AlgoTradeForge.IbApi.csproj`, AssemblyName `IBApi`).

It is marked as vendored via the repo-root `.gitattributes`
(`src/AlgoTradeForge.IbApi/** linguist-vendored=true`), so GitHub collapses it in
diffs and excludes it from language stats. **Do not edit these files** or "fix"
their warnings — they are re-fetched from IB, not authored here.

To bump the TWS API version:
1. Download the new **Stable** TWS API from https://interactivebrokers.github.io/
2. Replace `*.cs` (from `IBJts/source/CSharpClient/client/`) and `protobuf/*.cs`.
3. Keep `AlgoTradeForge.IbApi.csproj` as-is (nullable off, implicit-usings off,
   `TreatWarningsAsErrors=false` override, `Google.Protobuf` pinned) and the
   `bin/`+`obj/` `.gitignore`. Commit in this repo.

Vendored version: **10.45.01** (Google.Protobuf 3.29.5).
