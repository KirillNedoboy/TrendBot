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
tasks. Linux/systemd execution and a 24/7 soak remain unverified and are
deferred to P15-M01-T003 and later operational work.

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
