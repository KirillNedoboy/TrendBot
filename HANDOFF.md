# Current checkpoint

- Phase: 02 — Core Contracts
- Milestone: P02-M02 — Causal event and shared-risk contracts
- Current task: P02-M02-T001 (DONE)
- Next task: P02-M02-T002 (READY; first ready task with completed dependencies)

## Confirmed state

- Core remains BCL-only. Bar validates positive time span and OHLC bounds; Setup requires its anchor price to lie within the recorded high/low range; Position rejects update times before opening.
- `MarketEventComparison.Compare(previous, candidate)` is a pure diagnostic. Event identity is concrete type + InstrumentId + EventId; identical values yield Duplicate and different values with the same identity yield IdentityConflict. Other comparisons distinguish instrument and EventTime; equal EventTime remains SameEventTime without an EventId tie-breaker.
- `CausalMarketEvent` carries the immutable payload, local receive/availability timestamps, optional independent exchange timestamp, and non-negative stable sequence. Availability cannot precede receive. Sequence assignment, uniqueness, and monotonicity are scoped to one future outer recording stream.
- `CausalEventCursor` applies inclusive payload event-time, availability-time, and stable-sequence bounds. `CausalMarketEventOrdering.Compare` orders eligible events by payload EventTime and stable sequence; equal keys do not imply duplicate payloads. Exchange time remains unrelated to local bounds, and receive time is never substituted for event time.
- `CoreJsonSerializer` provides deterministic compact camelCase JSON for current Core contracts with strict numbers, string enums, rejected unknown members, required constructor parameters, `JsonException` for malformed/domain-invalid input, ordered immutable-list conversion, and `$type` discriminators `trade`, `bar`, `bookDelta`, `fundingUpdate`, and `oiUpdate` for `MarketEvent` values.
- Causal contracts remain passive: no replay runner, persistence envelope, quality flags, transition logic, risk math, Binance integration, or order side effects.
- The canonical final technical specification was not edited. Graft remains ignored and untouched.

## Verification evidence

- Locked restore: `dotnet restore tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj --locked-mode` — exit 0.
- Release build: `dotnet build tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --warnaserror` — exit 0, zero warnings and errors.
- Core tests: `dotnet test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-build --no-restore` — 34 passed, 0 failed.
- Format verification: `dotnet format TradingBot.slnx --no-restore --verify-no-changes` scoped to `src/TradingBot.Core/CausalEventContracts.cs` and `tests/TradingBot.Core.Tests/CausalOrderingTests.cs` — exit 0.
- Documentation validation: `pwsh -NoProfile -File tools/validate-docs.ps1` — exit 0; phases=19, milestones=41, tasks=119, ready=P02-M02-T002.
- `git diff --check` — exit 0; canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Remote GitHub Actions has not run and remains unverified. No commit, push, deployment, production mutation, or live financial operation was performed.
