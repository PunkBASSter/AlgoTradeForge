---
name: oop-first-design
description: Design guidance for AlgoTradeForge C#/.NET code. Consult BEFORE writing an enum+switch, an if/else-if tree, or any code that branches large blocks of behavior on a value or a runtime type. Decide whether the branch is really a set of polymorphic variants that should be modeled as types behind a shared interface, chosen at a single composition site — giving an extension point and a vertical slice per variant.
user-invocable: true
---

# Object-orientation & polymorphism first

**The rule:** before you write an `enum` + `switch`, or an `if / else if` chain that
selects between *substantial blocks of behavior*, stop and ask:

> Is this branch really a fixed axis of variation whose arms are *variants of the
> same shape*? If so, each arm wants to be a **type** implementing a shared
> interface, and one **composition site** (factory / registry / DI) should pick the
> implementation. Adding a variant then means *adding a file*, not editing a switch.

This is not "add interfaces everywhere." It is: **model the axes your product keeps
extending — venues, asset classes, data sources, order types, settlement modes,
strategy modules — as polymorphism seams**, so each new variant is a self-contained
vertical slice instead of a new arm threaded through every switch.

## When to stop and reach for polymorphism

Any one of these is a strong signal. Two or more means it is almost certainly the
right call:

1. **The same discriminator is switched on in more than one place.** `switch (kind)`
   / `if (type == X)` for the *same* concept appearing in 2+ methods or files is the
   loudest signal — the variants are begging to be objects (adding a variant is
   "shotgun surgery" across every switch).
2. **A branch arm is more than a few lines, or owns its own local state, I/O, or
   algorithm.** Big arms *are* classes. A one-line value map is not.
3. **Adding the next variant would mean editing existing branching code** rather than
   adding a new file (Open/Closed test). If "support venue Y" edits a switch body,
   the extension point is missing.
4. **The arms differ in behavior but share a stable input/output shape.** That common
   shape *is* the interface — it is already written, implicitly, in the switch.
5. **The discriminator is *inferred* rather than intrinsic.** Testing an incidental
   property as a proxy for "what kind of thing is this" (e.g. *"is the global 1m
   interval present in this manifest"* as a stand-in for *"is this an equity vs a
   crypto archive"*) means the real type should be a first-class object that carries
   its own behavior, not a tag reconstructed at each call.
6. **You are switching on a runtime type / subclass** and the arms hold logic
   (`switch (asset) { CryptoAsset => …, EquityAsset => … }`). Put the behavior on the
   hierarchy or on a per-type strategy; keep at most a *creation* switch.
7. **Tests must enumerate every arm.** If a new variant forces touching N test
   switches, coverage is coupled to the conditional — a sign the conditional should
   be a set of independently-testable types.
8. **The arms track a domain axis the product will keep extending.** Extension axes
   (asset classes, exchanges, data sources, broker sessions, bar kinds) are
   polymorphism seams by default — they are exactly where the next feature lands.

## When enum + switch / a plain conditional is the RIGHT answer

Do not over-abstract. Reach for a value + switch, or just an `if`, when:

- **It is an external or serialized contract.** Wire/JSON/DB enums, protocol
  discriminators, API request shapes — these are *data*. Keep them as enums. (If
  branching on them starts to grow, translate the enum into a polymorphic object once,
  at the boundary, and branch there — not at every consumer.)
- **It is a trivial, single-site value map with no behavior** — `enum → display
  string`, `enum → colour`, `mode → bool`. A `switch` *expression* returning a value
  is clean and honest; wrapping it in an interface is ceremony.
- **It is the single composition site that maps a discriminator to a polymorphic
  implementation** — a factory or registry: `asset switch { CryptoAsset or
  CryptoPerpetualAsset => _resampleFromSource, _ => _nativeElseDivisor }`. This one
  switch is the *destination* of the refactor, not a smell. The anti-pattern is that
  switch's **logic** reappearing inline at every call site; a single switch that
  selects a strategy object is correct and expected.
- **There is exactly one real implementation and no concrete second on the horizon**
  (YAGNI). A one-implementation interface is negative value. Leave a one-line note if a
  second variant is foreseeable, and refactor when it actually arrives.
- **The "variants" do not share a stable interface.** If forcing a common shape means
  a fat interface full of `NotSupportedException` arms, the conditional was telling the
  truth — keep it.
- **A measured hot path** where virtual dispatch costs more than the branch (rare;
  profile first — see the benchmark harness before claiming this).

## The shape of the good refactor

Replace-conditional-with-polymorphism does not *delete* the discriminator; it moves it
to one place and turns each arm into a testable type:

```
BEFORE — one method branches inline, and the same discriminator is re-derived elsewhere:

  Task<Resolution> Resolve(Asset asset, TimeFrame tf) {
      var hasSource = /* infer crypto-vs-equity from manifest contents */;
      if (hasSource) { /* 15 lines of crypto resample logic */ }
      else           { /* 20 lines of native-or-divisor logic  */ }
  }

AFTER — one interface, one file per variant, one factory to choose:

  interface IHistoryFeedResolver { Task<FeedResolution> Resolve(Asset a, TimeFrame tf, CancellationToken ct); }
  sealed class ResampleFromSourceResolver  : IHistoryFeedResolver { … }   // crypto slice
  sealed class NativeElseDivisorResolver   : IHistoryFeedResolver { … }   // equity/native slice

  sealed class HistoryFeedResolverFactory {
      public IHistoryFeedResolver For(Asset a) => a switch {          // the ONE allowed switch
          CryptoAsset or CryptoPerpetualAsset => _resampleFromSource, // — it returns a strategy,
          _                                   => _nativeElseDivisor,  //   it holds no logic itself
      };
  }
```

The payoff: a new data-source policy is a *new file* implementing the interface plus one
arm in the factory — not edits to `HistoryRepository`, its callers, and three tests.
`src/AlgoTradeForge.Infrastructure/History/IHistoryFeedResolver.cs` and its
implementations are the in-repo reference for this pattern.

## Process (do this at the moment you catch yourself typing `switch` / `else if`)

1. **Name the axis of variation** in one phrase ("resampling policy per data source",
   "settlement per asset class", "order routing per venue").
2. **Ask the two gating questions:** *Will this axis keep growing?* and *Is the same
   axis already switched somewhere else?* Either "yes" → polymorphism.
3. **If yes:** lift the common input/output into an interface, write one sealed impl per
   variant (one type per file — see the constitution's file-org rules; the slice *is*
   the file), and add exactly one factory/registry to choose. Route callers through the
   interface.
4. **If no** (single impl, trivial map, or non-behavioral contract): a local
   conditional is correct. If a second variant is plausible later, leave a terse
   `// TODO:` naming the axis so the seam is easy to find.

## Relationship to the rest of the codebase

- **Vertical slices over layered switches.** Prefer a folder that owns a variant's whole
  behavior (as `Strategy/Modules/`, `Assets/`, and the settlement calculators already
  do) over spreading its logic across a technical layer where each layer re-switches on
  the same tag.
- **Domain behavior lives on the domain object when the layer allows it.** Settlement
  dispatch (`asset.GetSettlementCalculator()`) is the model: the asset hierarchy answers
  "how do I settle" polymorphically instead of callers switching on `SettlementMode`.
  When layering forbids putting the behavior on the type (e.g. an Infrastructure
  resampling policy must not leak into a Domain `Asset`), use an Infrastructure-side
  strategy + factory instead — same pattern, correct layer.
- **External enums stay enums.** `DataFeedKind`, `AutoApplyType`, wire DTOs — these are
  contracts. The discipline is about *large behavioral branching*, not about deleting
  every enum.
