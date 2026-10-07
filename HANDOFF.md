# Current checkpoint

- Phase: 03 — Persistence and Replay Foundation
- Milestone: P03-M01 — Journal and deterministic replay envelope
- Current task: P03-M01-T001 (DONE)
- Next task: P03-M01-T002 (READY; first ready task with completed dependencies)

## Confirmed state

- Core remains BCL-only. Bar validates positive time span and OHLC bounds; Setup requires its anchor price to lie within the recorded high/low range; Position rejects update times before opening.
- `MarketEventComparison.Compare(previous, candidate)` is a pure diagnostic. Event identity is concrete type + InstrumentId + EventId; identical values yield Duplicate and different values with the same identity yield IdentityConflict. Other comparisons distinguish instrument and EventTime; equal EventTime remains SameEventTime without an EventId tie-breaker.
- `CausalMarketEvent` carries the immutable payload, local receive/availability timestamps, optional independent exchange timestamp, and non-negative stable sequence. Availability cannot precede receive. Sequence assignment remains caller-owned and stream-scoped.
- `CausalEventCursor` applies inclusive payload event-time, availability-time, and stable-sequence bounds. `CausalMarketEventOrdering.Compare` orders eligible events by payload EventTime and stable sequence; equal keys do not imply duplicate payloads. Exchange time remains unrelated to local bounds, and receive time is never substituted for event time.
- `CoreJsonSerializer` provides deterministic compact camelCase JSON for current Core contracts with strict numbers, string enums, rejected unknown members, required constructor parameters, `JsonException` for malformed/domain-invalid input, ordered immutable-list conversion, and `$type` discriminators `trade`, `bar`, `bookDelta`, `fundingUpdate`, and `oiUpdate` for `MarketEvent` values.
- `RiskMathVersion`, `MoneyPerQuantityUnit`, `RiskMathInput`, `RiskMathResult`, and `IRiskMathCalculator` define versioned, deterministic, side-effect-free sizing/exposure contracts shared across replay, paper simulation, and planning. Inputs snapshot the required instrument, direction, equity, risk fraction, entry/stop prices, fee, and slippage. Results include risk budget, loss per unit, raw pre-venue-constraint quantity, and notional exposure. Constructors enforce positive outputs and matching currencies without recomputing formulas; no execution mode, risk decision, sizing implementation, or trading defaults were added. Calculation parity remains P09-M02-T001.
- `FillSimulationVersion`, `FillSimulationAssumptions`, `FillSimulationInput`, `ResearchFill`, `FillSimulationResult`, and `IFillSimulator` define a separate passive research boundary. Inputs are identified hypothetical requests with positive quantity, optional reference price, request time, non-empty fee/slippage/latency/liquidity-queue/partial-fill/intrabar/data-limitation descriptions, and an immutable causal market snapshot. Snapshot events must match the instrument and be available by the request time. Results defensively retain the full input snapshot and only research fills; fill identifiers are unique and total filled quantity cannot exceed the hypothetical request. No fill contract references `OrderIntent`, `RiskDecision`, risk math, executor, or authorization state. Unknown assumptions remain prose, with no numeric defaults. Fill algorithms, replay integration, persistence, and paper execution remain future work.
- Core contracts remain passive: no replay runner, persistence envelope, quality flags, transition logic, Binance integration, or order side effects.
- `tests/Fixtures/CoreFixtureVectors.cs` remains a linked test-only source in `TradingBot.Core.Tests` and `TradingBot.Replay.Tests`, using only BCL APIs and `TradingBot.Core`.
- [`docs/architecture/core-contract-evidence.md`](docs/architecture/core-contract-evidence.md) records the current fixture identifiers and exact vector matrix. The strings are test examples, not released versions, and no global Core or wire-schema version exists.
- [`docs/architecture/persistence.md`](docs/architecture/persistence.md) records the SQLite schema, migration, append/read guarantees, and evidence limits for `SqliteEventJournal`.
- `SqliteEventJournal` is the Infrastructure recording boundary. It uses Microsoft.Data.Sqlite 10.0.12, opens one non-pooled connection per operation, persists canonical Core `MarketEvent` JSON plus event/receive/availability/optional exchange UTC ticks, and exposes `Open`, `Append`, and `ReadStream` without replay, risk, order, or execution dependencies.
- The journal uses transactional migration `0 -> 1`, `PRAGMA user_version`, WAL, `synchronous=FULL`, BINARY stream collation, and an immutable stable-sequence read snapshot. Equal retries return `false`; same-key conflicts and lower new sequences fail without insertion; corrupted payloads and mismatched causal columns raise `InvalidDataException`.
- The canonical final technical specification was not edited. Graft remains ignored and untouched.

## Verification evidence

- Startup protocol: worktree was clean at `4905b409`; P03-M01-T001 was the first `READY` task and P02-M03-T002 was `DONE`.
- Unlocked restore: `C:\temp\dotnet10\dotnet.exe restore TradingBot.slnx` — exit 0.
- Locked restore: `C:\temp\dotnet10\dotnet.exe restore TradingBot.slnx --locked-mode` — exit 0.
- RED evidence: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SqliteEventJournalTests"` failed before implementation with the expected missing `SqliteEventJournal` compile errors.
- Release Infrastructure.Tests build: `C:\temp\dotnet10\dotnet.exe build tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --warnaserror` — exit 0, zero warnings and errors.
- Infrastructure tests: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-build --no-restore` — 10 passed, 0 failed.
- Core boundary/project-graph build: `C:\temp\dotnet10\dotnet.exe build tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --warnaserror` — exit 0, zero warnings and errors.
- Core boundary/project-graph tests: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~CoreBoundaryTests|FullyQualifiedName~ArchitectureTests|FullyQualifiedName~ProductionProjectGraphMatchesApprovedModuleBoundaries"` — 4 passed, 0 failed.
- Changed C# format: `C:\temp\dotnet10\dotnet.exe format TradingBot.slnx --no-restore --verify-no-changes --include src/TradingBot.Infrastructure/SqliteEventJournal.cs tests/TradingBot.Infrastructure.Tests/SqliteEventJournalTests.cs` — exit 0.
- Documentation validation: `pwsh -NoProfile -File tools/validate-docs.ps1` — exit 0; phases=19, milestones=41, tasks=119, ready=P03-M01-T002.
- `git diff --check` — exit 0.
- Canonical source SHA-256: `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- GitHub Actions run [`37671835797`](https://github.com/KirillNedoboy/TrendBot/actions/runs/37671835797) for the preceding commit completed with failure on `Verify formatting` on both Windows and Ubuntu. The CI correction remains outside this task.

## Scope limits

- This task adds only the SQLite journal, migration/package wiring, focused Infrastructure tests, architecture/README documentation, and task handoff updates.
- Replay envelopes and causal replay execution, StrategyRun, ConfigVersion, Parquet, order intents, reservations, execution, and recovery remain future tasks.
- Persistence migration compatibility beyond version 1 and remote CI success remain unverified.
