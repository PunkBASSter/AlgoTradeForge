<!--
SYNC IMPACT REPORT
==================
Version change: 1.3.0 → 1.4.0
Modified principles: None
Added sections: None
Removed sections: None
Modified sections:
  - Backend > Code Style: Added "Int64 Money Convention" bullet — all monetary
    and price values within the Domain layer MUST use long (Int64). The
    Application layer is the decimal↔long conversion boundary via asset.TickSize.
    Quantities and percentages remain decimal.
Templates requiring updates:
  - .specify/templates/plan-template.md ✅ compatible (no money-type references)
  - .specify/templates/spec-template.md ✅ compatible
  - .specify/templates/tasks-template.md ✅ compatible
Follow-up TODOs: None
-->

# AlgoTradeForge Constitution

**Mission**: Fast track from ideas to live trading — providing a reliable, testable, and
observable pipeline for strategy development, optimization, verification, and deployment.

## Core Principles

### I. Strategy-as-Code

All trading strategies MUST be expressed as compilable C# code conforming to a
well-defined strategy interface. Strategies:

- MUST be self-contained compilation units with no external runtime dependencies beyond the strategy host SDK
- MUST declare their required indicators, parameters, and data dependencies explicitly
- MUST be stateless with respect to execution between bars; all order submission, position tracking, and fill observation MUST flow through the host-provided `IOrderContext`
- MAY maintain internal analytical state between bars (e.g., indicator buffers, bar history windows, derived signals) for decision-making purposes only; analytical state MUST NOT influence execution except through `IOrderContext` calls
- MUST NOT perform direct I/O; all market data and order execution flows through the host

**Rationale**: Dynamic compilation enables rapid iteration while maintaining type safety
and performance. Isolation ensures strategies can be backtested, optimized, and
deployed to live trading without code changes. The analytical/execution state distinction
enables strategies to implement indicators and lookback windows while ensuring all
execution actions remain observable and reproducible through the host interface.

### II. Test-First & Verification Pipeline (NON-NEGOTIABLE)

No strategy proceeds to live trading without passing the verification pipeline:

1. **Unit Tests**: Strategy logic tested against synthetic data scenarios
2. **Backtest**: Historical performance validated against defined benchmarks
3. **Walk-Forward Optimization**: Out-of-sample validation to detect overfitting
4. **Paper Trading**: Real-time execution against live data without capital risk
5. **Gradual Deployment**: Position sizing ramp-up with circuit breakers

- Tests MUST be written before strategy implementation (Red-Green-Refactor)
- Backtest results MUST be reproducible given identical inputs
- All verification stages MUST produce auditable artifacts

**Rationale**: Trading systems have direct financial consequences. Rigorous verification
prevents deployment of flawed strategies and builds confidence in live execution.

### III. Data Integrity & Provenance

Price data is the foundation of all decisions. Data handling MUST ensure:

- **Immutability**: Historical data MUST NOT be modified after initial ingestion
- **Versioning**: Data corrections create new versions, preserving audit trail
- **Completeness Checks**: Gaps MUST be detected, logged, and handled explicitly
- **Timestamp Precision**: All timestamps MUST be UTC with millisecond precision minimum
- **Source Attribution**: Every data point MUST track its origin and ingestion time

**Rationale**: Backtests are only as valid as the data they run on. Undetected data issues corrupt strategy evaluation and lead to false confidence.

### IV. Observability & Auditability

Every component MUST emit structured telemetry enabling debugging and compliance:

- **Logging**: Structured JSON logs with correlation IDs across all operations
- **Metrics**: Latency, throughput, error rates exposed via standard endpoints
- **Tracing**: Distributed tracing for request flows across frontend/backend/jobs
- **Trade Journals**: Every order, fill, and position change persisted with context
- **Debug Events**: Temporary diagnostic data with configurable TTL and auto-cleanup

**Rationale**: When money is at stake, you must be able to reconstruct exactly what happened. Observability enables debugging, compliance, and continuous improvement.

### V. Separation of Concerns

The system MUST maintain clear boundaries between components:

- **Frontend**: Visualization, user interaction, and workflow orchestration only
- **Backend API**: Strategy hosting, execution coordination, and business logic
- **Background Jobs**: Data ingestion, scheduled tasks, and async processing
- **Persistence**: Each data type has dedicated storage optimized for its access pattern

Frontend MUST NOT contain trading logic. Backend MUST NOT block on long-running
operations. Jobs MUST be idempotent and resumable.

**Rationale**: Clear boundaries enable independent scaling, testing, and deployment.
They prevent coupling that makes systems fragile and hard to evolve.

### VI. Simplicity & YAGNI

Complexity is the enemy of reliability. Every component MUST justify its existence:

- Start with the simplest solution that meets current requirements
- Add abstraction only when duplication becomes a maintenance burden
- Avoid speculative features; implement when needed, not "just in case"
- Prefer composition over inheritance; prefer functions over classes where appropriate
- Delete dead code immediately; commented-out code MUST NOT be committed

**Rationale**: Trading systems must be understood quickly during incidents. Every
unnecessary abstraction is a potential hiding place for bugs.

## Technology Standards

### Frontend (Next.js + TypeScript + Tailwind)

**Framework**: Next.js 15+ with App Router

- MUST use TypeScript strict mode with no `any` types in production code
- MUST use Server Components by default; Client Components only when interactivity required
- MUST use React Server Actions for mutations where appropriate
- MUST implement proper loading and error boundaries for each route segment

**Styling**: Tailwind CSS 4+

- MUST use Tailwind utility classes; custom CSS only for complex animations
- MUST define design tokens in `tailwind.config.ts` for consistency
- MUST NOT use inline styles or CSS-in-JS solutions

**Charting**: TradingView Lightweight Charts

- Chart components MUST be Client Components with proper cleanup on unmount
- MUST implement efficient data windowing for large datasets
- MUST handle real-time updates without full re-renders

**State Management**:

- Server state via React Query / TanStack Query with proper cache invalidation
- Client state via React Context or Zustand; Redux only if complexity warrants
- MUST NOT duplicate server state in client stores

**Code Organization**:

```
frontend/
├── app/                    # Next.js App Router pages and layouts
├── components/
│   ├── ui/                 # Reusable UI primitives
│   └── features/           # Feature-specific composite components
├── lib/                    # Utilities, API clients, helpers
├── hooks/                  # Custom React hooks
└── types/                  # TypeScript type definitions
```

### Backend (.NET 10 + ASP.NET Core)

**Framework**: .NET 10 with C# 14

- MUST use file-scoped namespaces and primary constructors
- MUST use `required` properties for mandatory initialization
- MUST use collection expressions (`[1, 2, 3]`) over explicit constructors
- MUST use pattern matching and switch expressions for control flow
- MUST use `readonly` and `init` where mutability is not required
- MUST use `Span<T>` and `Memory<T>` for high-performance data processing
- MUST NOT use `dynamic` type; prefer generics or discriminated unions

**Code Style**:
- MUST use self-descriptive naming; names should reveal intent without comments
- MUST name `CancellationToken` parameters as `ct` for brevity and consistency
- MUST NOT write XML summary comments (`<summary>`) or inline comments unless:
  - Explaining a non-obvious algorithm or mathematical formula
  - Flagging a known pitfall, workaround, or counterintuitive behavior
  - Documenting a `TODO` or `HACK` with justification
- Code MUST be self-documenting through clear naming, small methods, and explicit types
- MUST use `long` (Int64) for all monetary and price values within the Domain layer
  (cash, fill prices, commissions, equity curve). The Application layer converts
  `decimal` inputs to `long` via `(long)(value / asset.TickSize)` before calling
  the Domain engine, and converts `long` results back to `decimal` via
  `value * asset.TickSize` for the returned response. Quantities and percentages
  stay `decimal`.

**API Design**:

- MUST use minimal APIs for simple endpoints; controllers for complex resources
- MUST use strongly-typed request/response models (no anonymous types in APIs)
- MUST implement proper HTTP semantics (status codes, methods, caching headers)
- MUST version APIs via URL path (`/api/v1/`) for breaking changes

**Async/Concurrency**:

- MUST use `async`/`await` throughout; no `.Result` or `.Wait()` blocking
- MUST use `ct` propagation for all async operations
- MUST use `Channel<T>` or `IAsyncEnumerable<T>` for streaming scenarios
- MUST use `ValueTask<T>` for hot paths where allocation matters

**Dependency Injection**:

- MUST register services with appropriate lifetimes (Scoped for request, Singleton for shared)
- MUST use `IOptions<T>` pattern for configuration
- MUST NOT use service locator pattern; inject dependencies explicitly

**Strategy Hosting**:

- Strategy compilation MUST use Roslyn with security sandbox restrictions
- Compiled strategies MUST run in isolated `AssemblyLoadContext` for unloading
- MUST enforce memory and CPU limits per strategy execution
- MUST capture compilation errors with line/column information for user feedback

**Solution Layout** (repository root):

```
AlgoTradeForge/
├── AlgoTradeForge.slnx         # Solution file (XML format)
├── src/
│   ├── AlgoTradeForge.Domain/          # Domain models, interfaces, business logic
│   │   ├── Engine/                     # Backtest engine, bar matching
│   │   ├── History/                    # Data sources, bars, time series
│   │   │   └── Metadata/              # Bar and sample metadata
│   │   ├── Reporting/                  # Performance metrics
│   │   ├── Strategy/                   # Strategy interfaces and base types
│   │   └── Trading/                    # Orders, fills, positions, portfolio
│   ├── AlgoTradeForge.Application/     # Use cases (CQRS commands/queries)
│   │   ├── Abstractions/              # ICommand, IQuery, handler interfaces
│   │   ├── Backtests/                 # RunBacktest command + handler + DTOs
│   │   ├── CandleIngestion/           # IInt64BarLoader interface
│   │   └── Repositories/             # Repository interfaces
│   ├── AlgoTradeForge.Infrastructure/  # Data access, external services
│   │   ├── CandleIngestion/           # CsvInt64BarLoader (IInt64BarLoader impl)
│   │   └── History/                   # History data context
│   ├── AlgoTradeForge.CandleIngestor/ # Worker service (see note below)
│   │   ├── BinanceAdapter.cs          # Binance API adapter
│   │   ├── CsvCandleWriter.cs         # CSV partition writer
│   │   ├── IngestionOrchestrator.cs   # Fetch-and-store coordinator
│   │   ├── IngestionWorker.cs         # BackgroundService with PeriodicTimer
│   │   ├── RateLimiter.cs             # Sliding-window rate limiter
│   │   └── CandleIngestorOptions.cs   # Configuration records
│   └── AlgoTradeForge.WebApi/         # ASP.NET Core host, minimal API endpoints
│       ├── Contracts/                 # Request/response models
│       └── Endpoints/                 # Endpoint definitions
├── tests/
│   ├── AlgoTradeForge.Domain.Tests/   # Domain unit tests (xUnit + NSubstitute)
│   │   ├── Engine/                    # BacktestEngine, BarMatcher tests
│   │   ├── Trading/                   # Portfolio, Position tests
│   │   └── TestUtilities/             # Shared test data factories
│   └── AlgoTradeForge.Infrastructure.Tests/ # Infrastructure + CandleIngestor tests
│       └── CandleIngestion/           # CsvInt64BarLoader, CsvCandleWriter,
│                                      # BinanceAdapter tests
├── docs/                              # Design documents and requirements
└── specs/                             # Feature specifications and checklists
```

> **CandleIngestor architecture note**: `AlgoTradeForge.CandleIngestor` is a
> self-contained worker service that bundles its own infrastructure code
> (adapters, writers, rate limiter, configuration records) directly in the
> executable project. It references only Application (for `IInt64BarLoader`
> and domain types via transitive reference). This project is intentionally
> **exempt from clean architecture layering** — it is a thin utility service
> where simplicity and colocation outweigh separation of concerns. New
> exchange adapters, writers, or ingestion logic MUST be added here, not in
> the Infrastructure project.

**Code Organization conventions**:
- Each project uses namespace `AlgoTradeForge.<Layer>`
- Domain project exposes internals to its test project via `InternalsVisibleTo`
- CandleIngestor exposes internals to Infrastructure.Tests via `InternalsVisibleTo`
- Application references Domain; Infrastructure references Application;
  WebApi references Application, Domain, and Infrastructure;
  CandleIngestor references Application only
- Test projects mirror the source project folder structure

### Background Jobs

- MUST use a job framework with persistence (Hangfire, Quartz.NET, or similar)
- Jobs MUST be idempotent; re-running the same job produces identical results
- Jobs MUST checkpoint progress for resumability after failures
- MUST implement distributed locking for jobs that cannot run concurrently
- MUST have dead-letter queues for failed jobs with alerting

### Persistence

| Data Type | Storage | Retention | Access Pattern |
|-----------|---------|-----------|----------------|
| Price Data | TimescaleDB / ClickHouse | Indefinite | Time-series queries, bulk reads |
| Backtest Results | PostgreSQL + blob storage | 90 days default | Random access by ID |
| Optimization Results | PostgreSQL + blob storage | 90 days default | Filtered queries, comparisons |
| Trade Events | PostgreSQL | Indefinite | Audit queries, reporting |
| Debug Events | Redis / temp tables | 24-72 hours | Recent access, auto-expiry |
| User Sessions | Redis | Session duration | Key-value lookup |

- MUST use migrations for all schema changes (EF Core Migrations or Flyway)
- MUST NOT use ORM lazy loading; all data access explicit and eager
- MUST use read replicas for reporting queries to avoid impacting write performance

## Development Workflow

### Git Workflow

- `main` branch MUST always be deployable
- Feature branches MUST follow pattern: `feature/###-description`
- All changes MUST go through pull request review
- MUST NOT force-push to `main` or shared branches
- Commits MUST be atomic and pass all tests independently

### Code Review Requirements

- All PRs MUST have at least one approval before merge
- PRs MUST pass CI checks: build, lint, test, security scan
- PRs touching trading logic MUST have additional review from domain expert
- Reviewers MUST verify constitution compliance

### Testing Requirements

- Unit test coverage MUST exceed 80% for Core domain logic
- Integration tests MUST cover all API endpoints
- Performance tests MUST run nightly and flag regressions
- Strategy verification pipeline MUST have end-to-end tests

### Test Framework Stack

- MUST use **xUnit** as the test framework
- MUST use **NSubstitute** for mocking interfaces
- MUST use standard xUnit assertions (`Assert.Equal`, `Assert.Null`, etc.)
- MUST NOT use FluentAssertions or other assertion libraries

### CI/CD Pipeline

1. **Build**: Compile all projects, fail on warnings
2. **Lint**: ESLint (frontend), dotnet format (backend)
3. **Test**: Unit → Integration → E2E (parallelized where possible)
4. **Security**: Dependency vulnerability scan, SAST
5. **Deploy**: Staging → smoke tests → Production (with rollback plan)

## Governance

This constitution supersedes all informal practices and ad-hoc decisions. It represents
the collective agreement on how AlgoTradeForge is built and maintained.

### Amendment Process

1. Propose changes via pull request to this document
2. Changes MUST include rationale and impact assessment
3. Breaking changes (principle removal/redefinition) require team consensus
4. All amendments MUST update the version number per semantic versioning:
   - **MAJOR**: Principle removal or fundamental redefinition
   - **MINOR**: New principle added or significant expansion
   - **PATCH**: Clarifications, wording improvements, non-semantic changes

### Compliance

- All pull requests MUST be verified against applicable principles
- Constitution violations MUST be flagged and resolved before merge
- Exceptions require explicit documentation and team approval
- Technical debt tracking MUST reference violated principles

### Review Cadence

- Constitution review MUST occur quarterly or after major incidents
- Outdated principles MUST be updated or removed
- New patterns that emerge MUST be evaluated for inclusion

**Version**: 1.4.0 | **Ratified**: 2026-01-23 | **Last Amended**: 2026-02-19
