# TradingBot

Greenfield C#/.NET 10 modular monolith for deterministic, multi-lane Binance USD-M perpetual futures research and trading. The repository contains the Phase 0–3 project memory, passive Core contracts, and an SQLite event journal. Exchange calls, strategies, risk calculations, and order execution begin in later phases.

## Current state

- SDK: .NET 10.0.401, pinned in [`global.json`](global.json).
- Runtime target: `net10.0`.
- Solution: [`TradingBot.slnx`](TradingBot.slnx).
- P03-M01-T001 is complete; the next ready implementation task is `P03-M01-T002` (versioned deterministic replay envelope).
- Planning invariants: causal replay keeps event/receive/availability/stable-sequence fields distinct; shared risk math is separate from fill simulation; private execution, local paper, exchange test, and real-money activation are separate gates.
- Bounded Lane C/research capture and data-quality observability begin before private execution. Current Binance Algo Service and `ALGO_UPDATE` details remain a future official-source verification task.
- `graft/` is local service state and is ignored by Git.

## Project map

```text
src/TradingBot.Core              BCL-only shared kernel
src/TradingBot.Exchange.Binance  Exchange boundary, currently empty scaffold
src/TradingBot.Infrastructure    File-backed SQLite journal and persistence boundary
src/TradingBot.Backtesting        Replay/backtest boundary, scaffold only
src/TradingBot.App                Inert application entry point
tools/TradingBot.DataTool         Inert data-tool entry point
tests/*                           Architecture and startup smoke tests
docs/                             Specification, plan, backlog, decisions, Phase 0 notes
tools/tests/                      Built-in PowerShell validator fixtures
```

## Local checks

Documentation and planning correction checks:

```powershell
pwsh -NoProfile -File tools/tests/validate-docs.Tests.ps1
pwsh -NoProfile -File tools/validate-docs.ps1
Get-FileHash -Algorithm SHA256 docs/sources/FINAL_TECH_SPEC.md
git diff -- docs/sources/FINAL_TECH_SPEC.md
```

Bootstrap product checks remain available when a task changes the scaffold:

```powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
& $dotnet restore TradingBot.slnx
& $dotnet restore TradingBot.slnx --locked-mode
& $dotnet format TradingBot.slnx --verify-no-changes
& $dotnet build TradingBot.slnx -c Release --warnaserror
& $dotnet test TradingBot.slnx -c Release --no-build
```

Read [`AGENTS.md`](AGENTS.md) and [`HANDOFF.md`](HANDOFF.md) before starting a new task. Remote GitHub CI is configured for Windows and Linux but remains unverified until a remote run exists.
