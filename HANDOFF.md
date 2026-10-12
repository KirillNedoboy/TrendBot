# Current checkpoint

- Phase: 03 — Persistence and Replay Foundation
- Milestone: P03-M02 — Durable intent, reservation, and reconciliation records
- Current task: P03-M02-T001 (DONE)
- Next task: P03-M02-T002 (READY; first ready task with completed dependencies)

## Confirmed state

- Core remains BCL-only. Bar validates positive time span and OHLC bounds; Setup requires its anchor price to lie within the recorded high/low range; Position rejects update times before opening.
- `MarketEventComparison.Compare(previous, candidate)` is a pure diagnostic. Event identity is concrete type + InstrumentId + EventId; identical values yield Duplicate and different values with the same identity yield IdentityConflict. Other comparisons distinguish instrument and EventTime; equal EventTime remains SameEventTime without an EventId tie-breaker.
- `CausalMarketEvent` carries the immutable payload, local receive/availability timestamps, optional independent exchange timestamp, and non-negative stable sequence. Availability cannot precede receive. Sequence assignment remains caller-owned and stream-scoped.
- `CausalEventCursor` applies inclusive payload event-time, availability-time, and stable-sequence bounds. `CausalMarketEventOrdering.Compare` orders eligible events by payload EventTime and stable sequence; equal keys do not imply duplicate payloads. Exchange time remains unrelated to local bounds, and receive time is never substituted for event time.
- `CoreJsonSerializer` provides deterministic compact camelCase JSON for current Core contracts with strict numbers, string enums, rejected unknown members, required constructor parameters, `JsonException` for malformed/domain-invalid input, ordered immutable-list conversion, and `$type` discriminators `trade`, `bar`, `bookDelta`, `fundingUpdate`, and `oiUpdate` for `MarketEvent` values.
- `RiskMathVersion`, `MoneyPerQuantityUnit`, `RiskMathInput`, `RiskMathResult`, and `IRiskMathCalculator` define versioned, deterministic, side-effect-free sizing/exposure contracts shared across replay, paper simulation, and planning. Inputs snapshot the required instrument, direction, equity, risk fraction, entry/stop prices, fee, and slippage. Results include risk budget, loss per unit, raw pre-venue-constraint quantity, and notional exposure. Constructors enforce positive outputs and matching currencies without recomputing formulas; no execution mode, risk decision, sizing implementation, or trading defaults were added. Calculation parity remains P09-M02-T001.
- `FillSimulationVersion`, `FillSimulationAssumptions`, `FillSimulationInput`, `ResearchFill`, `FillSimulationResult`, and `IFillSimulator` define a separate passive research boundary. Inputs are identified hypothetical requests with positive quantity, optional reference price, request time, non-empty fee/slippage/latency/liquidity-queue/partial-fill/intrabar/data-limitation descriptions, and an immutable causal market snapshot. Snapshot events must match the instrument and be available by the request time. Results defensively retain the full input snapshot and only research fills; fill identifiers are unique and total filled quantity cannot exceed the hypothetical request. No fill contract references `OrderIntent`, `RiskDecision`, risk math, executor, or authorization state. Unknown assumptions remain prose, with no numeric defaults. Fill algorithms, replay integration, persistence, and paper execution remain future work.
- `ReplayEnvelope` is an immutable version-1 Core contract carrying its stream ID and complete `CausalMarketEvent`. `SqliteEventJournal` persists the envelope version and reads immutable envelope streams; migration 1 -> 2 assigns version 1 to prior rows. `ReplaySnapshotBuilder` filters with the inclusive causal cursor before ordering by event time and stable sequence. This is a bounded snapshot, not a full replay runner.
- `SqliteOrderIntentJournal` persists the complete Core `OrderIntent` as canonical JSON in the shared SQLite schema. Schema migration 2 -> 3 adds `order_intents` without rewriting `event_journal`; `IntentId` and `ClientOrderId` use BINARY uniqueness. Identical retries return false, identity conflicts roll back, and a successful append returns only after commit. This stage records only an intent awaiting future execution; it adds no adapter, submission, reservation, fill, or reconciliation behavior.
- `tests/Fixtures/CoreFixtureVectors.cs` remains a linked test-only source in `TradingBot.Core.Tests` and `TradingBot.Replay.Tests`, using only BCL APIs and `TradingBot.Core`.
- [`docs/architecture/core-contract-evidence.md`](docs/architecture/core-contract-evidence.md) records the current fixture identifiers and exact vector matrix. The strings are test examples, not released versions, and no global Core or wire-schema version exists.
- [`docs/architecture/persistence.md`](docs/architecture/persistence.md) records SQLite schema versions 1, 2, and 3, envelope and intent migrations, append/read guarantees, and evidence limits.
- `SqliteEventJournal` remains the Infrastructure event recording boundary and `SqliteOrderIntentJournal` is the durable intent boundary. Both use Microsoft.Data.Sqlite 10.0.12, one non-pooled connection per operation, canonical Core JSON, WAL, `synchronous=FULL`, and BINARY identities. Existing event rows receive envelope format version 1 transactionally; old event APIs remain available alongside envelope APIs.
- The canonical final technical specification was not edited. Graft remains ignored and untouched.

## Verification evidence

- Startup protocol: worktree was clean at `7c46c1b`; P03-M01-T002 was the first `READY` task and P03-M01-T001 was `DONE`.
- Unlocked restore: `C:\temp\dotnet10\dotnet.exe restore TradingBot.slnx` — exit 0.
- Locked restore: `C:\temp\dotnet10\dotnet.exe restore TradingBot.slnx --locked-mode` — exit 0.
- RED evidence: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SqliteEventJournalTests"` failed before implementation with the expected missing `SqliteEventJournal` compile errors.
- Release Infrastructure.Tests build: `C:\temp\dotnet10\dotnet.exe build tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --warnaserror` — exit 0, zero warnings and errors.
- Infrastructure tests: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-build --no-restore` — 10 passed, 0 failed.
- Core boundary/project-graph build: `C:\temp\dotnet10\dotnet.exe build tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --warnaserror` — exit 0, zero warnings and errors.
- Core boundary/project-graph tests: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~CoreBoundaryTests|FullyQualifiedName~ArchitectureTests|FullyQualifiedName~ProductionProjectGraphMatchesApprovedModuleBoundaries"` — 4 passed, 0 failed.
- Changed C# format: `C:\temp\dotnet10\dotnet.exe format TradingBot.slnx --no-restore --verify-no-changes --include src/TradingBot.Infrastructure/SqliteEventJournal.cs tests/TradingBot.Infrastructure.Tests/SqliteEventJournalTests.cs` — exit 0.
- Documentation validation: `pwsh -NoProfile -File tools/validate-docs.ps1` — exit 0; phases=19, milestones=41, tasks=119, ready=P03-M02-T001.
- P03-M01-T002 locked restore — exit 0; Core focused tests — 3 passed; Infrastructure tests — 12 passed; Replay tests — 5 passed; Release warnings-as-errors builds for Core.Tests, Infrastructure.Tests, and Replay.Tests — zero warnings/errors; scoped C# format — exit 0.
- P03-M01-T002 documentation validation: `pwsh -NoProfile -File tools/validate-docs.ps1` — exit 0; phases=19, milestones=41, tasks=119, ready=P03-M01-T003.
- `git diff --check` — exit 0.
- Canonical source SHA-256: `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- GitHub Actions run [`37671835797`](https://github.com/KirillNedoboy/TrendBot/actions/runs/37671835797) for the preceding commit completed with failure on `Verify formatting` on both Windows and Ubuntu. The CI correction remains outside this task.

## P03-M01-T003 evidence

- IntegrationTests adds a real SQLite write-close-reopen replay proof through SqliteEventJournal and ReplaySnapshotBuilder.
- Restart comparison covers envelopes, stable-sequence storage order, canonical JSON, causal snapshot order, and a deterministic projection string.
- Focused recovery cases cover idempotent repeat, same-key conflict, sequence gaps, late event time, unavailable events, corrupted payloads, write lock timeout, and no partial append after failure.
- Locked IntegrationTests restore passed; Release IntegrationTests build with warnings as errors passed with zero warnings and errors; all 5 IntegrationTests passed; scoped C# format verification passed.

## P03-M02-T001 evidence

- `SqliteOrderIntentJournal` opens the shared file-backed schema, appends canonical `OrderIntent` JSON transactionally, returns false for identical retries, rejects both IntentId and ClientOrderId conflicts without mutation, and finds intents after restart.
- Targeted tests cover all intent fields and decimal/null precision, schema 2 -> 3 migration with event preservation, shared event/intent use, rollback, and a one-second busy-writer failure with no partial row.
- Locked restore passed; Release Infrastructure.Tests build with warnings as errors passed with zero warnings and errors; focused intent tests passed 6/6 and all Infrastructure tests passed 18/18; scoped C# format verification passed.
- Documentation validation passed with first READY task P03-M02-T002; `git diff --check` passed; canonical `FINAL_TECH_SPEC.md` SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## Scope limits

- P03-M01-T002 adds only the versioned replay envelope, SQLite schema 1 -> 2 migration, deterministic bounded Backtesting snapshot, focused tests, architecture notes, and this handoff.
- P03-M02-T001 adds only durable order-intent state and the shared SQLite schema 2 -> 3 migration. Reservations, execution, fills, reconciliation, StrategyRun, ConfigVersion, Parquet, and recovery policy remain future tasks.
- Remote CI success remains unverified.
