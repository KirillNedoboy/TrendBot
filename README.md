# TradingBot

Public-only Binance USD-M signal research in a .NET 10 modular monolith. The
scanner can evaluate closed 5m/15m, 15m/1h, 30m/1h and 1h/4h setup/context
profiles, commits signals locally, and can deliver them to Telegram. It does
not place orders. The supplied profiles are exploratory: profitability and
statistical validation are unknown.

## Start the public signal bot

From the repository root in PowerShell, with the pinned .NET SDK installed:

```powershell
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
& $dotnet restore .\src\TradingBot.App\TradingBot.App.csproj --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Restore failed." }
& $dotnet build .\src\TradingBot.App\TradingBot.App.csproj -c Release --no-restore --warnaserror
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
New-Item -ItemType Directory -Force .\artifacts\signal-only-public | Out-Null
$app = ".\src\TradingBot.App\bin\Release\net10.0\TradingBot.App.dll"
& $dotnet $app signal-only --config .\signal-only.public.example.json
```

Run only one process against those state files. `public-stream-receiving`
means a public stream update was received, not that a signal was generated.
Closed candles and real committed signals appear in the console. In another
terminal, inspect the journal without publishing:

```powershell
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
$app = ".\src\TradingBot.App\bin\Release\net10.0\TradingBot.App.dll"
& $dotnet $app signal-only render --journal .\artifacts\signal-only-public\signal-journal.sqlite --symbol BTCUSDT --format console
```

Telegram is optional and explicitly enabled; see the
[operating instructions](docs/architecture/operations.md#public-signal-bot).
No OOS comparison is required to run this public research path. The process is
unbounded and has no installed supervisor or automatic startup; observe its
diagnostics, preserve its state files, and restart it if it exits.

The 12-symbol multi-timeframe profile is
[`signal-only.multitimeframe.example.json`](signal-only.multitimeframe.example.json):
it combines Lane A candidates with public Lane B/C evidence and vetoes for
each profile. After the build above, start it **instead of** the BTC-only
example, and stop any older process using the same state files first:

```powershell
New-Item -ItemType Directory -Force .\artifacts\signal-only-12 | Out-Null
& $dotnet $app signal-only --config .\signal-only.multitimeframe.example.json
```

Inspect one pair and setup timeframe without sending messages (`5m`, `15m`,
`30m`, or `1h`); omitting `--timeframe` retains the existing 15m behavior:

```powershell
& $dotnet $app signal-only render --journal .\artifacts\signal-only-12\signal-journal.sqlite --symbol ETHUSDT --format console --timeframe 5m
```

Its state is isolated under `artifacts/signal-only-12/`; do not run two writers
against the same state files. OI and trade-window warmup are required. Missing or
stale B/C evidence blocks that candidate rather than confirming it. The profile
is experimental, not statistically validated; see the [runtime details](docs/architecture/operations.md#p15-m03-t011-public-only-signal-runtime).

Each new signal can include a UTC-session VWAP, a POC for the observed
one-minute trade window, and the latest observed Binance liquidation snapshot.
They are context only, not extra entry filters. The liquidation stream is
sampled and a missing snapshot does not mean there were no liquidations.

## Current state

- SDK: .NET 10.0.401, pinned in [`global.json`](global.json); target `net10.0`.
- Research evidence, task status, and remaining gates are in [`HANDOFF.md`](HANDOFF.md) and [`docs/TASKS.md`](docs/TASKS.md).
- Public signal generation, research validation, and private/real-order authorization are separate.
- `graft/` is local service state and is ignored by Git.

## Project map

```text
src/TradingBot.Core              BCL-only shared kernel
src/TradingBot.Exchange.Binance  Binance public and private exchange boundaries
src/TradingBot.Infrastructure    File-backed SQLite journals and persistence
src/TradingBot.Backtesting       Replay and research reporting
src/TradingBot.App               Public signal and other explicit application modes
tools/TradingBot.DataTool        Data-tool entry point
tests/*                           Unit and integration tests
docs/                             Specification, plan, backlog, and decisions
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
