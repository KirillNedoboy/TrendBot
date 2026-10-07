# Current checkpoint

- Phase: 02 — Core Contracts
- Milestone: P02-M01 — Immutable domain contract foundation
- Current task: P02-M01-T002 (DONE)
- Next task: P02-M01-T003 (READY; first ready task with completed dependencies)

## Confirmed state

- Core remains BCL-only. Bar validates positive time span and OHLC bounds; Setup requires its anchor price to lie within the recorded high/low range; Position rejects update times before opening.
- `MarketEventComparison.Compare(previous, candidate)` is a pure diagnostic. Event identity is concrete type + InstrumentId + EventId; identical values yield Duplicate and different values with the same identity yield IdentityConflict. Other comparisons distinguish instrument and EventTime; equal EventTime remains SameEventTime without an EventId tie-breaker.
- The comparison does not establish processing order. Exchange/receive/availability times, stable sequence, replay, recovery, and serialization remain future task scope.
- Core contracts remain passive: no persistence, transition logic, risk math, Binance integration, or order side effects.
- The canonical final technical specification was not edited. Graft remains ignored and untouched.

## Verification evidence

- Locked restore: `dotnet restore tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj --locked-mode` — exit 0.
- Release build: `dotnet build tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --warnaserror` — exit 0, zero warnings and errors.
- Core tests: `dotnet test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-build --no-restore` — 20 passed, 0 failed.
- Format verification: `dotnet format` with `--no-restore --verify-no-changes`, scoped to `DomainContracts.cs` and `DomainInvariantTests.cs` in their respective projects — exit 0.
- Documentation validation: `pwsh -NoProfile -File tools/validate-docs.ps1` — exit 0; phases=19, milestones=41, tasks=119, ready=P02-M01-T003.
- Canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Remote GitHub Actions has not run and remains unverified. No commit, push, deployment, production mutation, or live financial operation was performed.
