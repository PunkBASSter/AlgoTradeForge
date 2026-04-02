# Contracts: Strategy Module Framework

No API contracts generated for this feature.

**Reason**: This is a domain-layer framework (strategy base class + modules). It introduces no new API endpoints, no new REST routes, and no new request/response models. Modular strategies implement the existing `IInt64BarStrategy` interface and are consumed by the existing backtest engine, optimization engine, and live connector without modification.

The "contracts" for this feature are the C# interfaces defined in `data-model.md`:
- `IFilterModule` — scored entry filter
- `IExitRule` — individual exit condition
- `ModularStrategyBase<TParams>` — abstract/virtual method signatures

These are code contracts, not API contracts, and are defined in the data model rather than as separate schema files.
