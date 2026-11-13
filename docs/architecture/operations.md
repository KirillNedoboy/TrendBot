# Operations baseline

The target deployment is a Linux VPS managed by systemd. Logs are structured JSON sent to journald; health, reconnect, stale-data, rate-limit, persistence, and kill-switch signals become observable in later phases. CI is configured for Windows and Linux through GitHub Actions but has no remote run in this bootstrap.

Docker, Kafka, Kubernetes, and ML are outside V1 scope. Secrets must come from the environment or a secret store and must never enter source, logs, docs, or test fixtures.

## P15-M01-T001 operational shadow runtime

The explicit `shadow` application mode is a long-running, cancellation-aware
process. It composes only the public Binance REST, market-stream, and depth-stream
clients, and it has no private dispatcher or order-submission dependency. The
`BinanceOperationalShadowMarketDataSource` emits causal public observations and
keeps stale, gap, disconnect, reconnect, and recovery state visible to the gate.
The source requires a REST depth snapshot before the stream is considered
recovered; after a process restart it resumes stable sequencing after the
persisted shadow checkpoint. No strategy, risk, or venue parameter is invented
by this adapter.

Each observation passes through `OperationalShadowGate` before the Core Decision
Engine and shared `RiskEvaluationEngine` are called. Missing evaluation/configuration,
stale data, pending reconnect or recovery, unavailable persistence, and an enabled
or unknown kill switch fail closed; a previously persisted enabled switch remains
effective after restart. A valid shadow configuration uses the Core
`DecisionEngineProfile.Shadow`; its result is never rewritten to `Allow`. Risk
evaluation remains passive evidence and cannot create an order. The cycle result
records a hypothetical-entry observation separately from the invariant real-order
count, which is always zero.

`OperationalShadowMetrics` uses BCL `System.Diagnostics.Metrics` instruments for
cycles, blocked cycles, hypothetical entries, and an observable
`shadow.real_order_count` gauge fixed at zero. The command writes compact JSON
startup/stop/fault records to standard output so systemd/journald receives
structured lines. `deploy/systemd/tradingbot-shadow.service` is declarative only:
it uses `Type=simple`, `Restart=on-failure`, a non-root `tradingbot` user,
`StateDirectory`, and journald output; it performs no installation or deployment.

Operational shadow runtime and service verification is Windows-only in these
tasks. P15-M01-T003 covers a bounded local soak; Linux/systemd execution and
a 24/7 soak remain unverified for later operational work.

## P15-M01-T002 health, metrics, and reconnect signals

`OperationalShadowHealthSnapshot` is the immutable App health surface. It
records the checked-at time, per-feed connection/freshness state, last receive
and availability timestamps, stale and sequence-gap flags, reconnect and
recovery state, persistence health, kill-switch knowledge and value, effective
gate state and reasons, the invariant zero real-order count, and the explicit
`NotApplicableInShadow` private-reconciliation status. The runtime exposes the
snapshot through `HealthSnapshot` and notifies `HealthChanged`; the initial
snapshot is `Unknown` and an empty or incomplete health provider fails closed.

`IOperationalShadowHealthSource` lets public market and depth adapters publish
their states independently. Binance disconnect, reconnect, stale, gap, parse,
frame, and recovery diagnostics invalidate only the affected feed, while the
combined runtime gate requires every required feed to be fresh, connected, and
recovered. A depth gap creates a recovering order-book state and requires a
fresh snapshot/diff recovery. If either required stream ends unexpectedly, the
source cancels the peer pump and faults the command for systemd restart.

The runtime refreshes health on source changes and on a five-second timer, so
stale data remains visible even when no market messages arrive. JSON health
records are written beside startup, cycle, stop, and fault records for
journald. `OperationalShadowMetrics` uses BCL `System.Diagnostics.Metrics`
gauges for health status, stale/gap, reconnect/recovery, persistence, gate,
kill-switch, reconciliation applicability, and real-order count; no private
dispatcher or real-order surface is introduced.

## P15-M01-T003 bounded shadow soak and recovery

The local acceptance fixture processes 256 deterministic public observations
across four process lifetimes, reopening the isolated SQLite store between
lifetimes. It compares the complete journal with an uninterrupted run: stream
and event identities, event/exchange/receive/availability times, stable
sequence, derived state, decision/risk outcome, and checkpoint JSON must match.
The final persisted checkpoint is sequence 255, and retrying the preceding
observation after each restart is idempotent. Reconnect and gap health close
the hypothetical-entry gate until both feeds report fresh recovery. A held
SQLite writer closes the gate without a partial append; retry after release
persists once. Runtime and structured command records keep real order count
at zero throughout. The fixture uses no private adapter or real network.

These checks provide bounded local recovery evidence only. Real Binance
connectivity, Linux/systemd execution, and a 24/7 soak are not yet verified.

## P15-M02-T001 shadow data-quality health evidence

`IOperationalShadowHealthSource.RequiredFeedStreams` is the explicit required
public-stream identity contract. The runtime deduplicates the required set and
counts a required stream as covered only when both receive and availability
evidence exist. Duplicate required rows and extra source rows cannot increase
the denominator or covered count. A null required set is unknown coverage,
represented by a null ratio and a fail-closed `CoverageUnknown` gate reason;
known partial coverage reports deterministic per-stream missing reasons and
uses `CoverageIncomplete`. Existing stale-after, gap, reconnect, recovery,
`NotApplicableInShadow`, and zero-real-order semantics remain unchanged.

Coverage counts and reasons are present on `OperationalShadowHealthSnapshot`
and `HealthChanged`, in compact JSON health lines, and in BCL gauges
`shadow.coverage_known`, `shadow.required_feed_count`,
`shadow.covered_feed_count`, and `shadow.coverage_ratio` (NaN when the ratio is
unknown). Health JSON also carries the runtime run identity and health sequence.

The isolated `OperationalShadowStore` now uses schema v2. New databases create
the additive `shadow_health_evidence` table directly; v1 files are migrated in
one transaction without altering `shadow_entries` or `shadow_checkpoints`.
Each health row contains versioned JSON, a unique run ID, a strictly increasing
health sequence within that run, checked-at time, and an optional causal
checkpoint link. Runtime construction, source changes, cycles, and the health
timer append evidence; timer-only rows have no causal link. Evidence-write
failure marks persistence unhealthy, publishes the degraded snapshot, and
closes subsequent hypothetical-entry gates. A new runtime instance gets a new
run ID after restart. This is durable local evidence only; no private execution,
audit export, real Binance connectivity, Linux/systemd execution, or long soak
is claimed.

## P15-M02-T002 auditable shadow gate export

`OperationalShadowStore.OpenReadOnly` opens an existing shadow file without
creating or migrating it. `ReadAuditSnapshot(runId)` reads the schema version,
all health rows for exactly that run, and only journal rows referenced by the
health causal checkpoint links inside one deferred read-only SQLite transaction.
Health row metadata, evidence versions, causal timestamps, and referenced
checkpoint fields are validated before the snapshot is returned. A missing
health sequence or an unresolved causal link is retained as an explicit audit
blocker; malformed metadata, unsupported versions, and any nonzero real-order
count fail closed.

`OperationalShadowAuditEvidence` is immutable `operational-shadow-audit-v1`
JSON. Its identity is SHA-256 over the canonical body, while the exported
UTF-8 bytes are also content-addressed and written through a same-directory
CreateNew temporary file followed by an atomic no-overwrite move. An identical
retry is idempotent; a conflicting destination or SQLite/WAL/SHM path is
rejected. The offline `shadow-audit-export --database <path> --run-id <id>
--output <file>` command returns 0 for a completed export even when blockers
are present, 1 for an export/refusal error, and 2 for bad arguments.

The promotion adapter records the operational-shadow category, run and audit
identity, provenance, blockers, and explicit exclusions. It grants no private
or real-order execution authority. Export evidence remains synthetic/local;
private reconciliation, real Binance connectivity, Linux/systemd operation,
and long-duration soak remain outside this task.

## P15-M03-T011 public-only signal runtime

Start the isolated signal path with `signal-only --config <public-config.json>`.
The strict format-v1 root requires mode `signal-only-public`, a complete explicit
Binance USD-M native-kline profile and scenario-policy set, unique symbols within
the declared universe bounds, and separate file-backed paths for the input ledger
and signal journal. Unknown or duplicate properties and private transport settings
are rejected before either database is opened.

The command composes the existing public REST and market WebSocket clients. Each
symbol has one `SignalOnlyInstrumentConsumer` with per-instrument serialization.
An empty ledger triggers REST warmup; a populated ledger restores feed history and
replays pending accepted closed 15m receipts in durable intake order. Only a newly
accepted closed 15m WebSocket input triggers existing Lane A evaluators and the
scenario/quality projection. Closed 1h WebSocket context is retained with its
durable payload hash for causal signal provenance.

Confirmed setups atomically commit the signal/evidence, scenario checkpoint and
events, cursor, and output-only outbox through `SqliteSignalJournalStore`;
non-confirmed inputs commit diagnostics instead. A crash after input-ledger append
but before journal commit is recovered by replaying the original receipt, while a
committed journal checkpoint prevents a duplicate signal/outbox on restart. This
path has no private transport, signer, executor, risk authorization, order intent,
position, or portfolio dependency. The existing `shadow` command and its gates are
unchanged. Public live connectivity and Linux/systemd operation remain unverified.

## P15-M03-T018 bounded public-run preflight

`signal-only preflight --envelope <file>` checks a strict, versioned declaration
without opening a database or contacting an exchange. The declaration binds the
complete signal profile and scenario policies, public symbol universe, UTC start
window, time and resource limits, isolated storage, freshness and retry policies,
observed outputs, and the required disconnect, gap, database-failure, restart,
and graceful-stop drills. Unknown mandatory values, unsupported policies,
private settings, inconsistent bounds, a nonempty or non-isolated run directory,
and insufficient currently available disk space block readiness. Cancellation
does not create the declared stores.

The verdict identifies the exact envelope, configuration, profile and scenario
policy hashes. `ready` means only that the declaration and the current local
preflight checks passed: it is not evidence that a bounded runtime has started,
that disk space will remain available, or that resource limits are enforced.
The separate `signal-only --config` command is unbounded and must not be used as
a substitute for the prospective bounded run. T019 must enforce the declared
limits, check public instrument eligibility, and provide the actual health,
output, recovery and replay evidence before its own acceptance.

## P15-M03-T019 bounded-run implementation

`signal-only bounded-run --envelope <file> --sha256 <hash>` requires the exact
preflighted envelope and a fresh, isolated run directory. The first process may
use `--pause-after-first-close`; `--resume` reopens only the same run, envelope,
assembly identities and original deadline. An exclusive run lock prevents
concurrent writers. Unknown or unexpected files and reparse points block
restart rather than being overwritten. The run declares its assembly hashes,
decision-clock outcome policy and resource sub-budgets before public requests.

Only the fixed Binance USD-M public REST and market WebSocket clients are
composed; redirects are disabled and current exchange metadata must admit the
declared symbol. REST pages and response reads, WebSocket frame/message sizes,
the number of input receipts and the run deadline are bounded. Only actual
closed 15m/1h WebSocket observations trigger the serial consumer; open updates
are not signal inputs. A disconnect, gap, conflicting revision, persistence
failure or stale required feed stops the run instead of authorizing a signal
or inventing a backfill. A fresh SQLite input ledger, live journal and replay
journal use separate page limits and DELETE rollback journals; logs and
outcomes have separate budgets. The total logical file size is monitored.
Working set and total files are sampled at the declared health interval and
again at completion, not enforced as an OS-level instantaneous quota.

Health distinguishes expected and observed prospective close counts and
reports unknown quality while coverage is unresolved. Planned restart and
completion wait for both timeframes to cover every interval at the same cutoff,
including a shared close received in either order; missing input remains subject
to the declared freshness and deadline bounds. A committed journal is rendered
locally without an external publication receipt. Only actual
committed signals can produce an outcome observation: its decision clock,
15-minute horizon, scenario expiry and available cancellation are declared
before the public run, with no target or fill assumptions. Completion waits
for closed, available 15m input through every committed signal horizon; an
unobserved horizon at the run deadline stops without creating an outcome.
Native Core bar closes are already end-exclusive and are not extended. Outcome
usability is the maximum of the original recorded availability, event time and
bar close: an early closed receipt cannot expose the bar before its close, and
the original receipt and event timestamps remain unchanged. Missing or
incomplete future paths remain MissingPath/Censored, not a return. The
final independent journal replay uses recorded causal inputs and must match the live
semantic snapshot. Both snapshots use `CoreJsonSerializer`; generic Web JSON
rejects the immutable required-nullable `SignalRecord.Probability` contract.
T019 acceptance evidence is recorded in `HANDOFF.md`: a predeclared public-only
run completed with actual 15m/1h inputs, a committed signal, local output and a
decision-clock outcome, plus independent causal-prefix and restart parity.
Earlier failures and NoSetup windows remain retained. No external publication,
OOS comparison, order or realized-profit claim follows from this evidence.

## P15-M03-T021 public-only start, recovery and verdict

### Verdict boundaries

| Gate | Verdict | Evidence boundary |
|---|---|---|
| Software baseline | PASS for the T020 input-to-outcome, restart, idempotency, failure, local-output and zero-private/order-call checks | Release tests and the isolated real-ledger replay are software evidence, not strategy validation |
| Research | `RESEARCH`; statistical validation `UNKNOWN` | T019 observed one genuine signal and one decision-clock OHLC outcome over one available bar. No OOS comparison, validated edge, or realized-profit claim follows |
| Execution authorization | Not granted by this runbook or by software readiness | No private execution, orders, paper/testnet or external publication/send |

T019's outbox entry is a durable local output row; it has no external delivery
receipt and no publication was observed. T014's Telegram acknowledgment applies
only to its separate TESTONLY message. It is not T019 publication evidence.

### Operator procedure

Use only a separately authorized, predeclared public envelope with an active
UTC start window, isolated fresh run directory and frozen profile/policies.
Do not edit an envelope after preflight, reuse the expired T018/T019 declaration
for a new run, or treat preflight `ready` as runtime readiness. The public-only
path needs no exchange credentials. It reads public REST/market-WebSocket data
and writes only local stores/output.

From the repository root in PowerShell, after building the pinned SDK's Release
App:

```powershell
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
& $dotnet restore --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Locked restore failed." }
& $dotnet build .\src\TradingBot.App\TradingBot.App.csproj -c Release --no-restore --warnaserror
if ($LASTEXITCODE -ne 0) { throw "Release App build failed." }
$app = ".\src\TradingBot.App\bin\Release\net10.0\TradingBot.App.dll"
$envelope = (Resolve-Path -LiteralPath $env:TRADINGBOT_PUBLIC_ENVELOPE).Path
$hash = (Get-FileHash -LiteralPath $envelope -Algorithm SHA256).Hash.ToLowerInvariant()
& $dotnet $app signal-only preflight --envelope $envelope
if ($LASTEXITCODE -ne 0) { throw "Public envelope preflight failed." }
```

Read the preflight JSON and require `status` = `ready`; retain its envelope
hash and reasons with the run evidence. Preflight validates the declaration,
does not create the stores, contact the exchange, start a process or guarantee
runtime resource availability.

For a planned restart drill, start with the declared envelope and its exact
hash:

```powershell
& $dotnet $app signal-only bounded-run --envelope $envelope --sha256 $hash --pause-after-first-close
if ($LASTEXITCODE -ne 0) { throw "Bounded public run did not reach its planned stop." }
```

The planned stop waits until both timeframes cover the same cutoff; it may
therefore occur after the first 15m close. Resume the same run using the same
envelope, hash, run directory and identical Release assemblies:

```powershell
& $dotnet $app signal-only bounded-run --envelope $envelope --sha256 $hash --resume
```

The run lock prevents concurrent writers. Resume preserves the original
deadline and refuses changed envelope/assembly identity, unexpected files,
reparse points or an expired deadline. On disconnect, gap, conflicting
revision, stale required feed or SQLite failure, stop and preserve the run
directory and logs; do not edit the declaration, delete evidence, or infer
missing inputs. Resume only when the original identity/deadline checks pass.
Otherwise retain the failed run and request a separately authorized, newly
predeclared public capture if needed.

Inspect local output read-only using the `signalJournalPath` declared inside the
same envelope:

```powershell
$wire = Get-Content -LiteralPath $envelope -Raw | ConvertFrom-Json -Depth 64
$journal = [string]$wire.configuration.signalJournalPath
& $dotnet $app signal-only render --journal $journal --symbol $wire.configuration.symbols[0] --format console
```

The renderer does not publish. A pending output is not a delivery receipt;
`publishedAt` remains null without a separate publication event. `signal-only
telegram-send` is a different external side effect and is outside this
runbook; never invoke it without separate explicit authorization. Do not use
the unbounded `signal-only --config` command as a substitute for bounded run.

The isolated-store smoke for T020/T021 copied the actual T019 native ledger,
restored the feed/consumer into a fresh local journal, compared the result to
the persisted T019 journal and outcome, and ran the local renderer with zero
HTTP calls. It verifies offline recovery and local presentation, not a second
live capture. T019 itself separately records its actual bounded process
restart. Evidence and exact test counts are in [HANDOFF.md](../../HANDOFF.md) under T020.
