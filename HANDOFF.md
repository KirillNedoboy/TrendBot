# Current checkpoint

- Phase: 02 — Core Contracts
- Milestone: P02-M02 — Causal event and shared-risk contracts
- Current task: P02-M02-T003 (DONE)
- Next task: P02-M03-T001 (READY; first ready task with completed dependencies)

## Confirmed state

- Core remains BCL-only. Bar validates positive time span and OHLC bounds; Setup requires its anchor price to lie within the recorded high/low range; Position rejects update times before opening.
- `MarketEventComparison.Compare(previous, candidate)` is a pure diagnostic. Event identity is concrete type + InstrumentId + EventId; identical values yield Duplicate and different values with the same identity yield IdentityConflict. Other comparisons distinguish instrument and EventTime; equal EventTime remains SameEventTime without an EventId tie-breaker.
- `CausalMarketEvent` carries the immutable payload, local receive/availability timestamps, optional independent exchange timestamp, and non-negative stable sequence. Availability cannot precede receive. Sequence assignment, uniqueness, and monotonicity are scoped to one future outer recording stream.
- `CausalEventCursor` applies inclusive payload event-time, availability-time, and stable-sequence bounds. `CausalMarketEventOrdering.Compare` orders eligible events by payload EventTime and stable sequence; equal keys do not imply duplicate payloads. Exchange time remains unrelated to local bounds, and receive time is never substituted for event time.
- `CoreJsonSerializer` provides deterministic compact camelCase JSON for current Core contracts with strict numbers, string enums, rejected unknown members, required constructor parameters, `JsonException` for malformed/domain-invalid input, ordered immutable-list conversion, and `$type` discriminators `trade`, `bar`, `bookDelta`, `fundingUpdate`, and `oiUpdate` for `MarketEvent` values.
- `RiskMathVersion`, `MoneyPerQuantityUnit`, `RiskMathInput`, `RiskMathResult`, and `IRiskMathCalculator` define versioned, deterministic, side-effect-free sizing/exposure contracts shared across replay, paper simulation, and planning. Inputs snapshot the required instrument, direction, equity, risk fraction, entry/stop prices, fee, and slippage. Results include risk budget, loss per unit, raw pre-venue-constraint quantity, and notional exposure. Constructors enforce positive outputs and matching currencies without recomputing formulas; no execution mode, risk decision, sizing implementation, or trading defaults were added. Calculation parity remains P09-M02-T001.
- `FillSimulationVersion`, `FillSimulationAssumptions`, `FillSimulationInput`, `ResearchFill`, `FillSimulationResult`, and `IFillSimulator` define a separate passive research boundary. Inputs are identified hypothetical requests with positive quantity, optional reference price, request time, non-empty fee/slippage/latency/liquidity-queue/partial-fill/intrabar/data-limitation descriptions, and an immutable causal market snapshot. Snapshot events must match the instrument and be available by the request time. Results defensively retain the full input snapshot and only research fills; fill identifiers are unique and total filled quantity cannot exceed the hypothetical request. No fill contract references `OrderIntent`, `RiskDecision`, risk math, executor, or authorization state. Unknown assumptions remain prose, with no numeric defaults. Fill algorithms, replay integration, persistence, and paper execution remain future work.
- Core contracts remain passive: no replay runner, persistence envelope, quality flags, transition logic, Binance integration, or order side effects.
- The canonical final technical specification was not edited. Graft remains ignored and untouched.

## Verification evidence

- Locked restore: `dotnet restore TradingBot.slnx --locked-mode` — exit 0.
- Release build: `dotnet build TradingBot.slnx -c Release --no-restore --warnaserror` — exit 0, zero warnings and errors.
- Release Core.Tests build: `C:\temp\dotnet10\dotnet.exe build tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --warnaserror` — exit 0, zero warnings and errors.
- Focused Core tests: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FillSimulationContractTests|FullyQualifiedName~RiskMathContractTests|FullyQualifiedName~CoreBoundaryTests|FullyQualifiedName~ArchitectureTests"` — 15 passed, 0 failed.
- Format verification: `C:\temp\dotnet10\dotnet.exe format TradingBot.slnx --no-restore --verify-no-changes --include src/TradingBot.Core/FillSimulationContracts.cs src/TradingBot.Core/DomainValues.cs tests/TradingBot.Core.Tests/FillSimulationContractTests.cs` — exit 0.
- Documentation validation: `pwsh -NoProfile -File tools/validate-docs.ps1` — exit 0; phases=19, milestones=41, tasks=119, ready=P02-M03-T001.
- `git diff --check` — exit 0; canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Remote GitHub Actions has not run and remains unverified. No commit, push, deployment, production mutation, or live financial operation was performed.
