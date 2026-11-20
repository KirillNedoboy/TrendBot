# Operations baseline

The target deployment is a Linux VPS managed by systemd. Logs are structured JSON sent to journald; health, reconnect, stale-data, rate-limit, persistence, and kill-switch signals become observable in later phases. CI is configured for Windows and Linux through GitHub Actions but has no remote run in this bootstrap.

Docker, Kafka, Kubernetes, and ML are outside V1 scope. Secrets must come from the environment or a secret store and must never enter source, logs, docs, or test fixtures.

## Hourly Donchian-flow research profile

`signal-only.donchian-flow.example.json` is an isolated public-only format-7
profile for 25 USDT perpetuals: the multi-strategy example's original 12 plus
TRX, LTC, BCH, DOT, NEAR, APT, ARB, OP, INJ, ATOM, ETC, UNI and FIL.
It uses new intake and signal databases under `artifacts/donchian-flow-25-sl13-tp89/`;
do not point them at existing strategy streams or earlier Donchian-flow runs.
Before the first launch, create that directory: the runtime does not create
database parent directories. After reviewing the configuration, history bounds,
disk usage and destination, run:
`dotnet run --project src/TradingBot.App -c Release -- signal-only --config signal-only.donchian-flow.example.json`.
No deployment, order, fill, protective order or profitability is implied.
The fixed history start makes warmup and storage grow with time; 25 concurrent
instruments increase feed, CPU and storage demands. Each symbol has its own
serial stream. `bodyFloor` is shared across symbols in price units, so
cross-symbol sensitivity must be studied rather than assumed. The fixed list
is not a validated universe-selection rule.

`signal-only.donchian-flow-30m.example.json` is a separate 30m experiment
on the same 25 instruments, with the same bar-count formulas, 13/89 model
exits and 96-hour scenario expiry. Its one-hour market and availability age
limits correspond to two 30m bars. It uses
`artifacts/donchian-flow-30m-25-sl13-tp89/`; create this directory before
launch and do not reuse hourly stores. The author described the 9/34 parameters
for 1h, not 30m: the transfer is an unvalidated hypothesis. No orders or
automatic deployment are implied.

Both hypotheses use closed and available setup candles (1h or 30m). The
breakout compares the current high/low strictly against the highest high/lowest
low of the preceding 55 candles. Flow is
`volume/max(abs(close-open),bodyFloor)`; its current-inclusive SMA(10)
normalizes flow, which is multiplied by close before WMA(9)/WMA(34).
Volume must be strictly greater than 1.5 times the current-inclusive SMA(20).
WMA equality and simultaneous up/down breakouts produce no entry. The example's
`bodyFloor=0.000001` is an explicit price-unit research assumption, not an
author-verified constant. Tie handling, prior-only channel and current-inclusive
normalization are assumptions; the author's original script was not provided.

At least 56 contiguous eligible bars are required. The stream is setup-only:
it neither loads nor subscribes to the declared context (4h for the hourly profile,
1h for the 30m profile), and Lane B/C are not admission gates. Readiness checks
the setup history and freshness, not the unused context. The existing
configuration schema retains a context declaration; setup-only provenance
format 3 contains no context reference and its HTF cut field is not applicable.
Older binaries may reject format-3 records, including 30m setup-only records.
Use separate databases and preserve compatible binaries and backups for rollback.

Signal evidence records channel boundaries, WMA values, current and mean volume,
and the candle extremes. The 13/21/89/144 boundaries are preceding-bar channel
references, not orders or guaranteed executable stops/targets; 89/144 remain
unknown without sufficient continuous history. A target based on channel 89
requires 89 prior contiguous bars plus the signal bar. The currently active scenario
is canceled when a later closed candle breaches the opposite 55 boundary,
irrespective of volume or trend. The example enables `stopChannelBars=13`
and `targetChannelBars=89`; these model fixed boundaries at the signal cut.
The stop uses the boundary itself, without an extra offset. Other supported
choices are 21 and 144 respectively; null disables each model. An unavailable
level, or a level on the wrong side of the reference close, remains unknown.
A later candle touching both a target and a stop/opposite boundary cancels with
`channel-exit-ambiguous`,
without asserting which occurred first. The signal candle cannot end its own
scenario, including after recovery. These are alert lifecycle observations,
not positions, fills, returns or executable protection. Scenario rearm, expiry,
delivery, and recovery use the declared passive policies.

The legacy `bounded-run`/preflight composition is not adapted to format 7 and
explicitly rejects this profile. Signal research remains RESEARCH/UNKNOWN; OOS
comparison, epsilon sensitivity, fees, latency and funding still require a
separately frozen study with suitable data. The bundled independent Pine file is
not verified as the author's original algorithm.

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
The strict root requires mode `signal-only-public`, a complete explicit Binance
USD-M native-kline profile and scenario-policy set, unique symbols within the
declared universe bounds, and separate file-backed input-ledger and journal
paths. Format v1 uses the profile's common boundary width. Format v2 additionally
requires a positive, complete `boundaryMinimumChannelWidths` map keyed by the
exact configured symbols. Format v3 also requires an explicit multi-lane research
policy. Format v4 adds explicit independent setup/context profiles and scenario
policies. Format v5 additionally requires independently selectable strategy
timeframes and explicit research parameters. Format v6 adds an explicit
price-level policy for every signal family and requires matching ATR settings on
every configured timeframe. These policies participate in the effective profile
hash. Formats v1–v5, legacy v6, and isolated v7 remain readable. Unknown or
duplicate properties and private transport settings are rejected before either
database is opened.
Root format v8 adds risk-scaled price-level policy format v2. It first computes
the existing ATR/wick stop distance, then multiplies that entire distance by
each family's explicit `stopDistanceMultiplier`; TP distances use the scaled
risk and the existing target ratios. The multitimeframe example applies 2 to
Flow ATR and Flow Stochastic across every enabled timeframe, and 1 elsewhere.
With their existing target ratios, these families now indicate SL at 2× the
original risk, TP1 at 2×, and TP2 at 4×. Format v6 with nested policy v1 keeps
the unscaled formula for historical configurations; root format v7 remains the
isolated Donchian-flow profile. These are indicative levels, not orders or
execution authority.

`signal-only.flow-25.example.json` is a separate 25-symbol research profile
for Flow ATR and Flow Stochastic only, on 15m, 30m and 1h. It retains the
format-v8 risk-scaled levels and uses separate stores. The 13 added symbols
were checked against Binance exchange information on 2026-11-20; their minimum
widths are the observed price ticks, not calibrated strategy parameters.
The original 12 overlap the multi-strategy profile, so running both with
delivery enabled can duplicate alerts. Check resource capacity, state isolation
and delivery scope before adding either workload to a service.

`signal-only.12.example.json` is a format-v3 exploratory profile for the 12
configured perpetual contracts. Its widths are one price tick per symbol
observed on 2026-11-13; this does not establish strategy equivalence or
profitability, and the application does not automatically refresh exchange
filters. Recheck contract eligibility and tick sizes before operation. The
bounded preflight and bounded run currently accept format v1 only.

The command composes the existing public REST and market WebSocket clients. A
v4–v6 instrument has one consumer and durable stream per setup timeframe, but one
deduplicated kline WebSocket and one shared public B/C data source. The v3
15m/1h stream retains its existing identity. Empty streams warm up from REST;
populated streams restore their own feed and recover pending receipts in intake
order. Only newly accepted closed setup bars trigger decisions. Closed, available
context inputs retain their durable payload hashes for causal provenance.

`signal-only.multitimeframe.example.json` adds 5m/15m, 30m/1h and 1h/4h to
the original 15m/1h profile on the same 12 symbols. The five native kline
subscriptions per symbol are shared across profiles. Indicator periods are
deliberately specified in bars rather than equal wall-clock spans: using the
baseline counts on another timeframe is an exploratory starting hypothesis,
not a calibrated equivalence. Freshness is two bars of the relevant timeframe;
scenario expiry retains the baseline 96 setup bars (8h, 24h, 48h, 96h for
5m, 15m, 30m, 1h respectively). OI polling and the C trade window remain shared
one-minute observations at each decision, not timeframe-specific evidence
of effectiveness. The new profiles start history at 2026-11-09 04:00 UTC;
the original 15m/1h seed and version are unchanged.

The v6 example retains Boundary Rejection and Center Retest on all four setup
timeframes. Failed-breakout reclaim, breakout/retest continuation and three
independent Flow variants run on 15m, 30m and 1h; compression/expansion runs on
30m and 1h. The timeframe arrays in `researchStrategies` independently enable
each family. Empty arrays disable that family. A policy hash is included in the
new family identity and the complete policy participates in the profile hash.
All candidates use the same explicit B/C confirmation/neutral/veto policy; no
new family can authorize an order.

Failed-breakout reclaim freezes a confirmed pivot level known before the breach
bar opens and before its input sequence. A bar must cross and close through
that level; a strictly later closed bar must reclaim it. Same-bar returns are
not credited. Breakout/retest requires a close across a known level, a later
bar touching and holding it, and a further close beyond the retest extreme.
Both directions are mirrored and unfinished episodes expire after the configured
number of bars. Missing levels, gaps and incompatible causal sequences remain
unknown, not confirmations.

Flow recovery has separate stochastic, RSI and ATR-normalized families. They
use closed OHLC inputs, explicitly configured lookback, smoothing, scale and
extreme levels. A signal requires a prior extreme and a later return without
jumping into the opposite extreme; incomplete warmup and zero denominators are
unknown. The example's Flow settings are research seeds from the supplied
reference, not optimized parameters. These independent decimal implementations
do not claim TradingView/Prizrak numerical parity.

Compression/expansion compares normalized ATR-channel width to its previous
configured history: the preceding bar must set a strict window low, the current
width must expand, and the current close must cross the preceding high or low.
The example uses its existing ATR-period length as the research lookback.
Unsupported channel configuration or incomplete history is unknown. These rules
are exploratory hypotheses, not OOS results or calibrated recommendations.

**Indicative research SL and targets.** Format v6 records the signal bar's
closed reference price `E` and the causally available setup-timeframe ATR(14)
with Wilder/RMA smoothing. With signal-bar low `L`, high `H`, family multiplier
`k`, and `A = ATR(14)`, the initial levels are:

* Long: `SL = min(E - k*A, L - 0.1*A)`.
* Short: `SL = max(E + k*A, H + 0.1*A)`.
* `R = abs(E - SL)`; TP1 is `E +/- 1*R` and TP2 is `E +/- p*R`, with plus
  for long and minus for short. Levels are fixed at the signal decision; this
  is not a trailing stop or a fill simulation.

| Family | Setup timeframes | `k` | TP2 `p` |
| --- | --- | ---: | ---: |
| Boundary Rejection | 5m, 15m, 30m, 1h | 1.5 | 2 |
| Center Retest | 5m, 15m, 30m, 1h | 1.5 | 2.5 |
| Failed Breakout Reclaim | 15m, 30m, 1h | 1.5 | 2 |
| Breakout Retest Continuation | 15m, 30m, 1h | 2 | 2.5 |
| Flow Stochastic, RSI and ATR Recovery | 15m, 30m, 1h | 1.5 | 2 |
| Volatility Compression Expansion | 30m, 1h | 2 | 2.5 |

The 0.1 ATR wick buffer and target multiples are explicit, unoptimized design
choices, not values established by the cited literature or profitability
evidence. ATR adapts to each symbol and timeframe; 14 bars are not the same
elapsed time across timeframes. Fidelity describes ATR(14), Wilder smoothing
and 1.5 ATR as an example volatility-based stop in its
[ATR guide](https://www.fidelity.com/learning-center/trading-investing/technical-analysis/technical-indicator-guide/ATR).
Carver, *Systematic Trading* (2015), and Aronson, *Evidence-Based Technical
Analysis* (2007), motivate explicit rules and guard against overfitting; neither
validates these settings for Binance perpetuals.

The signal bar must be the latest causal ATR input. Missing/gapped data,
nonpositive or unrepresentable prices and arithmetic overflow yield `Unknown`
without numeric levels; the signal itself is not retroactively removed.
The levels are not rounded to an exchange tick and are not executable-order
prices. Binance's [PRICE_FILTER](https://developers.binance.com/docs/derivatives/usds-margined-futures/common-definition)
uses `minPrice`, `maxPrice` and `tickSize`; the existing boundary-width map is
not a live filter source. Reference close is not an entry fill, and a stop price
does not guarantee execution at that price. Spread, slippage, fees, funding,
latency and profitability are not calculated.


Older binaries cannot deserialize new family names or signal-record format v2.
Preserve a consistent pre-upgrade journal backup before cutover; never open a
journal containing new-format signals with an older binary.

The optional `public-market-context-v1` is explanatory and never changes signal
admission. UTC-session VWAP is `sum(quoteAssetVolume) / sum(baseVolume)` from
contiguous closed native setup bars from the start of the UTC day through the
signal bar. At midnight the just-closed bar belongs to the completed previous
day. This is exchange-reported volume, not a typical-price OHLCV approximation;
it is distinct from the existing `PriceContextFeatureCalculator` trade-window
VWAP. Missing bars, unavailable inputs, zero volume and overflow yield explicit
unknown values. The older calculator's unconfigured VWAP/POC diagnostics remain
separate and do not mean that this optional context used a guessed fallback.

Observed POC uses the already configured one-minute Lane C aggregate-trade
window, price bins anchored at zero with the configured per-symbol minimum
channel width (one observed tick in the supplied profile), and lower-bin tie
breaking. Event, exchange and availability times must be
visible at the cut. Missing window coverage or unavailable source timestamps
make POC unknown. This is a recent observed-trade profile, not a full-session
POC or proof of market-wide volume coverage; exchange tick sizes are not refreshed
automatically.

An independent public `<symbol>@forceOrder` stream supplies optional liquidation
context. Binance publishes at most the largest snapshot per symbol per 1,000 ms;
these are not complete liquidation flow or modeled liquidation levels. Only a
recent snapshot visible at the decision cut is retained. No observation means
`not observed`, not zero liquidations; disconnect clears the optional snapshot
without changing the B/C streams. Source:
[Binance liquidation stream](https://developers.binance.com/legacy-docs/derivatives/usds-margined-futures/websocket-market-streams/Liquidation-Order-Streams).
New signal evidence format v3 binds this context to the decision; old v1/v2
evidence and hashes remain unchanged. Context is shown in close logs, local cards
and optional Telegram messages; it is not reconstructed from later data.


The v3 and v4 profiles consume public mark-price/funding forecasts, open interest,
aggregate trades, and a sequence-recovered depth book. Lane B compares two
distinct OI observations and mark prices visible when those observations arrived:
rising OI with price moving in the setup direction is `Confirm`, rising OI with
opposite price movement is `Veto`, and non-rising OI or an unchanged price is
`Neutral`. Funding is recorded as context, not treated as directional proof.
Lane C compares displayed top-N book imbalance with aggressor-volume imbalance:
both aligned are `Confirm`, both opposed are `Veto`, and mixed or zero signs are
`Neutral`. A Lane A candidate is admitted only when both B/C are Confirm or
Neutral; Veto, missing or stale observations, incomplete trade-window coverage,
book recovery and conflicting data block it.

Depth continuation follows Binance USD-M's `pu == previous u` rule; adjacent
update-ID ranges need not be numerically consecutive. A broken `pu` link
invalidates the book and requires recovery:
[Binance local order book procedure](https://developers.binance.com/legacy-docs/derivatives/usds-margined-futures/websocket-market-streams/How-to-manage-a-local-order-book-correctly).

The profile explicitly specifies the polling interval, event/availability
freshness, trade window, depth levels and maximum captured records. These are
experimental operating values, not inferred profitability or a validated strategy.
The aggregate-trade stream and book checkpoints are not an event-level OFI
series or proof of complete market coverage. Research inputs and rule versions
are retained with successful signal evidence in the same journal transaction.
The rationale and transfer limits are documented in the repository's
[literature review](../../additional-strategy-research-plan.md#9-литература-что-перечитано-и-что-действительно-следует):
Cont–Kukanov–Stoikov study contemporaneous U.S. equity order-flow impact;
Cartea–Jaimungal–Penalva distinguish displayed liquidity from execution and
cancellation; Aronson distinguishes objective rules from evidence of their
predictive value. None validates this Binance 15-minute experiment.

For a crash after price intake but before multi-lane evidence is committed,
recovery records `multi-lane-recovery-unavailable` rather than constructing an
old signal from newly observed market data.

Transient public REST failures invalidate the affected OI or book state and
retry only after the configured polling interval or a longer `Retry-After`;
other inputs do not become confirmation. Source-observed log entries show
arrival, not a signal or a guarantee of continued health. Non-transient API
failures stop the process and report the status/code/path without response bodies.
The `trade-window-buffer-overflow` diagnostic means the bounded trade buffer was
cleared and the full observation window must warm again before Lane C can be
usable. The `trade-window-awaiting-*` and `trade-window-warming` diagnostics
identify missing warmup prerequisites; they are not confirmations.
No supervisor, storage quota, 24/7 soak or real-money authorization is implied.

Confirmed setups atomically commit the signal/evidence, scenario checkpoint and
events, cursor, and output-only outbox through `SqliteSignalJournalStore`;
non-confirmed inputs commit diagnostics instead. Formats v1/v2 recover an input
appended before journal commit by replaying its original receipt; v3/v4 follow
the fail-closed multi-lane recovery rule above. A committed checkpoint prevents duplicate
signal/outbox rows. This path has no private transport, signer, executor, risk
authorization, order intent, position, or portfolio dependency. The existing
`shadow` command and its gates are unchanged. Public connectivity was observed
in bounded runs; Linux/systemd operation and long-duration service remain unverified.

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

## Public signal bot

For the continuously running public-only command, use the quick start and
exploratory BTCUSDT profile in the [README](../../README.md). Its fixed history
start is part of the declared profile and its recursive indicator seed; do not
shift it or change parameters while reusing the same journals. This mode is not
a bounded T019 acceptance run and does not authorize an OOS verdict or orders.

`public-stream-receiving` records that a matching WebSocket update arrived;
`public-close` records its ingestion result and, for a setup-timeframe evaluation,
readiness and unknown reasons. Both close and diagnostic lines include the
profile's `setupTimeframe`. A committed signal is also written as a `signal` JSON
line with direction, setup, decision time, signal ID and `RESEARCH` / `UNKNOWN`.
`public-stream-diagnostic` contains only a diagnostic code, not raw transport
messages. Open candle updates are not committed as signal inputs. An early
closed update waits until its nominal close without rewriting its original
receive or availability time.

On restart, the consumer first recovers any already committed input, then
backfills missing native history from public REST. The first open update after
subscription also triggers backfill, covering a bar that closed between REST
warmup and WebSocket subscription. After a reconnect or a detected history gap,
recovery is attempted on a subsequent open update; incomplete retries are
throttled to 30 seconds. Recoverable stream diagnostics clear only when both
native histories are complete. A close received while recovery is unresolved
remains unknown; backfilled bars do not retroactively generate signals. An
unresolved error, missing history or conflicting revision must not be interpreted
as a valid signal.

The market client has bounded reconnect attempts and the process exits on
unrecovered failure. There is no installed service, resource limit or long-running
soak guarantee. Keep one process per state directory, monitor its output and
restart it when necessary. Preserve the input and signal journals together.

### Explicit Telegram delivery

Telegram delivery is off unless `--telegram-state` is provided. Stop any
existing process using the same journals before starting this variant. Before
running it, verify the intended destination: the command sends eligible
committed outbox messages, including pending messages still within the existing
24-hour delivery policy. Enter the bot token and destination privately in the
terminal, not in source files, logs or chat:

```powershell
$secure = Read-Host "Telegram bot token" -AsSecureString
$env:TRADINGBOT_TELEGRAM_BOT_TOKEN = [System.Net.NetworkCredential]::new("", $secure).Password
$secure = Read-Host "Telegram chat ID" -AsSecureString
$env:TRADINGBOT_TELEGRAM_CHAT_ID = [System.Net.NetworkCredential]::new("", $secure).Password
Remove-Variable secure
& $dotnet $app signal-only --config .\signal-only.public.example.json --telegram-state .\artifacts\signal-only-public\telegram-delivery.sqlite
```

The examples assume the `$dotnet` and `$app` variables from the README in the
same PowerShell session. Environment values last for that session; use a
proper secret store for a supervised service. The sender polls committed rows
and persists attempts and receipts separately. `Acknowledged` means the API
accepted a message, not that a person read it. `UnknownDelivery` is not retried
automatically because the message may already have been accepted. Rate limits,
expiry and permanent failures remain explicit in the delivery journal.

For continuous v4 delivery, newly created alerts for the same instrument,
direction and exact candle close are grouped when they are available within the
five-second co-close window. Each signal and outbox item retains its own durable
attempt and receipt; opposite directions and different closes remain separate.
Late arrivals may form separate messages. A crash after recording a grouped
attempt remains ambiguous for the affected items and does not trigger an
automatic resend. One-shot `telegram-send` still sends one selected stream.

Keep the original delivery state alongside the signal journal. If a journal
already has outbox rows but its delivery state is missing, the continuous
command refuses to create a replacement rather than risk duplicate delivery.
The local journal renderer does not report Telegram receipts as publication.
