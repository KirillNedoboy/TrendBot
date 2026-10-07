# Execution plan

This plan turns [`PROJECT_SPEC.md`](PROJECT_SPEC.md) into independently verifiable work. Milestones and task counts are intentionally variable: a milestone is split when it has a separate contract, evidence boundary, or recovery risk. A task is `DONE` only after its acceptance criteria and listed validation pass.

The startup selector chooses the first `READY` task in document order whose dependencies are `DONE`. Multiple `READY` tasks are valid when their dependencies are complete. Zero `READY` tasks is valid only when the backlog records an explicit unresolved blocker; an ordinary dependency chain is not an implicit blocker.

Every replay-facing milestone preserves the causal tuple of event time, exchange time when available, receive/availability time, and stable processing sequence. Shared risk math is kept separate from fill simulation. Private execution, local paper simulation, exchange test execution, and real-money activation are separate gates.

## Phase 00 — Specification Freeze

### P00-M01 — Source, requirements, and architectural baseline

- Goal: preserve the final 108-section source specification and index stable requirements.
- Dependencies: none.
- Implement: source archive, requirements index, Phase 0 architecture notes, ADR catalog, unknowns, and missing references.
- Acceptance: source copy is byte-preserved; requirement IDs are unique and covered; contradictions and unknown values are explicit; `docs/decisions` is the canonical ADR path.
- Validation: source SHA-256, docs validator, and review of the requirement/ADR index.
- Tasks: `P00-M01-T001` source archive; `P00-M01-T002` requirements index; `P00-M01-T003` architecture/ADR baseline.

## Phase 01 — Repository Bootstrap

### P01-M01 — Buildable solution and local CI contract

- Goal: create the exact project graph and a repeatable local validation path without product behavior.
- Dependencies: P00-M01.
- Implement: pinned SDK, local Git `main`, five production projects, five test projects, DataTool, central package/build settings, lock files, README, CI, and smoke tests.
- Acceptance: Core is BCL-only; project graph is acyclic and matches approved boundaries; App/DataTool only print scaffold status; `graft/` is ignored and untouched.
- Validation: restore, locked restore, format, Release build with warnings as errors, tests, and docs validation.
- Tasks: `P01-M01-T001` scaffold; `P01-M01-T002` boundary/smoke tests; `P01-M01-T003` verification and handoff.

### P01-M02 — Approved planning correction and graph handoff

- Goal: resolve the planning contradictions before Phase 2 while leaving the product bootstrap inert.
- Dependencies: P01-M01.
- Implement: corrected requirements/ADRs; independently verifiable Phase 2–18 roadmap; task graph and dynamic validator; fixture cases and CI invocation; current handoff.
- Acceptance: every invariant has a requirement/task/ADR trail; all P01-M02 tasks are `DONE`; P02-M01-T001 is the first `READY` task after this milestone; the canonical source remains unchanged.
- Validation: focused validator fixtures, current validator, source hash/diff, and review of the graph and handoff. Remote GitHub CI remains unverified.
- Tasks: `P01-M02-T001` specification and ADR correction; `P01-M02-T002` roadmap and backlog; `P01-M02-T003` dynamic validator and fixtures; `P01-M02-T004` verification and handoff.

## Phase 02 — Core Contracts

### P02-M01 — Immutable domain contract foundation

- Goal: define the minimum value types and event contracts used by every later lane.
- Dependencies: P01-M02.
- Implement: immutable InstrumentId, Price, Quantity, Money, UTC timestamp, MarketEvent, Trade, Bar, BookDelta, FundingUpdate, OIUpdate, Setup, Decision, RiskDecision, OrderIntent, and Position contracts with explicit local invariants.
- Acceptance: invalid local values are rejected, value equality and decimal precision are deterministic, collections are immutable snapshots, serialization is added by P02-M01-T003, and Core still has no non-BCL references.
- Validation: unit, property/invariant, serialization, and compile-boundary tests.
- Tasks: `P02-M01-T001` base Core types; `P02-M01-T002` event/value invariants; `P02-M01-T003` contract serialization tests.

### P02-M02 — Causal event and shared-risk contracts

- Goal: make causal replay fields and shared risk semantics explicit in Core contracts.
- Dependencies: P02-M01.
- Implement: event/receive/availability/stable-sequence fields, quality flags, risk-math versioning, and a side-effect-free fill-simulation boundary.
- Acceptance: equal-time events have deterministic order; replay envelopes carry the ordering tuple; fill assumptions cannot authorize an order.
- Validation: invariant, replay-order, serialization, and boundary tests.
- Tasks: `P02-M02-T001` causal timing contract; `P02-M02-T002` shared risk-math contract; `P02-M02-T003` fill-simulation boundary.

### P02-M03 — Core boundary evidence

- Goal: verify that the expanded contracts remain reusable and dependency-safe.
- Dependencies: P02-M02.
- Implement: contract compatibility notes and deterministic fixture vectors.
- Acceptance: every new invariant has an executable fixture and no exchange/runtime dependency enters Core.
- Validation: architecture and deterministic contract checks.
- Tasks: `P02-M03-T001` boundary fixtures; `P02-M03-T002` contract evidence report.

## Phase 03 — Persistence and Replay Foundation

### P03-M01 — Journal and deterministic replay envelope

- Goal: persist artificial events and reproduce them exactly.
- Dependencies: P02-M03.
- Implement: SQLite migrations, journal, StrategyRun, ConfigVersion, replay envelope, and Parquet boundary.
- Acceptance: an artificial stream survives restart and replays in causal order with no lookahead.
- Validation: persistence, restart, serialization, replay, and SQLite busy/failure tests.
- Tasks: `P03-M01-T001` SQLite journal; `P03-M01-T002` replay envelope; `P03-M01-T003` deterministic persistence tests.

### P03-M02 — Durable intent, reservation, and reconciliation records

- Goal: establish durable state before any future side effect.
- Dependencies: P03-M01.
- Implement: order-intent journal, atomic portfolio reservations, fills/positions, reconciliation checkpoints, and unknown/manual-position markers.
- Acceptance: restart preserves intent and reservation state; a partial fill cannot release more capacity than reserved; unknown state blocks retry until reconciled.
- Validation: transaction, restart, concurrency, partial-fill, and recovery tests.
- Tasks: `P03-M02-T001` durable order intents; `P03-M02-T002` atomic reservations; `P03-M02-T003` reconciliation records.

## Phase 04 — Binance Public Market Data

### P04-M01 — Public REST/WebSocket adapter

- Goal: normalize current Binance USD-M public market data.
- Dependencies: P03-M02.
- Implement: exchangeInfo, server time, tickers, bars, trades, mark/funding, OI, liquidations, depth, and local orderbook recovery.
- Acceptance: current official schemas, weights, auth requirements, reconnect behavior, and sequence rules are documented and fixture-tested.
- Validation: exchange fixtures, rate limits, reconnect, stale stream, and orderbook gap/recovery tests.
- Tasks: `P04-M01-T001` REST market data; `P04-M01-T002` WebSocket streams; `P04-M01-T003` orderbook/reconnect fixtures.

### P04-M02 — Private protocol catalog and Algo Service check

- Goal: capture current private API facts before any implementation depends on them.
- Dependencies: P04-M01.
- Implement: official endpoint catalog, current Binance Algo Service notes, `ALGO_UPDATE` event/schema fixture, auth/rate/error/freshness matrix.
- Acceptance: unknown fields remain marked unknown; the catalog records the verification date and source; no SDK is introduced into Core.
- Validation: documentation/source review and fixture schema checks.
- Tasks: `P04-M02-T001` current endpoint catalog; `P04-M02-T002` Algo Service/`ALGO_UPDATE` fixture; `P04-M02-T003` freshness and error matrix.

### P04-M03 — Bounded early research capture

- Goal: begin Lane C/research evidence from public data before private execution exists.
- Dependencies: P04-M01.
- Implement: bounded capture manifest, retention/provenance rules, gap/duplicate markers, and quality counters.
- Acceptance: capture is versioned and bounded; it has event/receive/stable-sequence fields; it has no dependency on private execution and emits no orders.
- Validation: capture/restart, manifest, gap, duplicate, and quality-observability fixtures.
- Tasks: `P04-M03-T001` bounded capture envelope; `P04-M03-T002` provenance and retention; `P04-M03-T003` quality counters.

## Phase 05 — Universe Manager

### P05-M01 — Candidate universe pipeline

- Goal: select and refresh eligible perpetual symbols with deterministic metadata filters.
- Dependencies: P04-M02.
- Implement: eligibility metadata, metadata-only candidate filters, and versioned selection snapshots.
- Acceptance: metadata eligibility is reproducible and does not consume future feature data; existing candidate-filter and snapshot tasks remain auditable.
- Validation: fixture, determinism, rate-limit, and restart tests.
- Tasks: `P05-M01-T001` eligibility metadata; `P05-M01-T002` candidate filters; `P05-M01-T003` selection snapshots.

### P05-M02 — Minimum screener feature contract

- Goal: define the smallest feature set needed by screening without creating a dependency on Phase 5 from the feature engine.
- Dependencies: P05-M01-T001.
- Implement: bounded lookbacks, warmup/quality state, and versioned screener feature snapshots.
- Acceptance: screener features are computed only from available data, expose warmup/quality state, and can be replayed independently.
- Validation: feature golden, no-lookahead, and restart checks.
- Tasks: `P05-M02-T001` minimum screener features; `P05-M02-T002` screener freshness/quality; `P05-M02-T003` screener snapshots.

### P05-M03 — Staged selection

- Goal: apply staged candidate reduction only after metadata eligibility and minimum screener evidence exist.
- Dependencies: P05-M02.
- Implement: independently explainable selection stages and provenance for each stage.
- Acceptance: stage boundaries are deterministic, versioned, and do not use future data; no Phase 5/6 dependency cycle exists.
- Validation: staged fixture, determinism, rate-limit, and provenance checks.
- Tasks: `P05-M03-T001` first selection stage; `P05-M03-T002` second selection stage; `P05-M03-T003` final selection stage.

## Phase 06 — Feature Engine

### P06-M01 — Price and context features

- Goal: calculate reusable market context from normalized events.
- Dependencies: P05-M01-T003 and P05-M03-T003.
- Implement: ATR, moving averages, pivots, RVOL, VWAP, POC, dynamic levels, channel, and trend state.
- Acceptance: each feature declares lookback and warmup; calculations are deterministic and no-lookahead; final periods remain configuration experiments.
- Validation: golden fixtures, property/invariant, and replay-parity tests.
- Tasks: `P06-M01-T001` rolling indicators; `P06-M01-T002` levels/channel/context; `P06-M01-T003` feature golden tests.

### P06-M02 — Feature quality and readiness evidence

- Goal: make feature freshness, gaps, warmup, and provider quality visible to downstream lanes.
- Dependencies: P06-M01.
- Implement: data-quality status, readiness reasons, coverage metrics, and replay-visible quality events.
- Acceptance: stale, incomplete, duplicated, or gapped inputs cannot silently produce a ready feature; evidence is persisted with the feature version.
- Validation: quality fixtures, stale-data tests, and replay parity checks.
- Tasks: `P06-M02-T001` feature quality state; `P06-M02-T002` readiness observability; `P06-M02-T003` quality regression fixtures.

## Phase 07 — Lane A V1

### P07-M01 — Price/structure setups

- Goal: implement only Boundary Rejection and Center Retest setup families.
- Dependencies: P06-M02.
- Implement: setup observations, structural conditions, invalidation, and setup evidence.
- Acceptance: both directions are explicit; no experimental setup family leaks into V1; evidence is serializable.
- Validation: unit, replay, property, and no-lookahead tests.
- Tasks: `P07-M01-T001` Boundary Rejection; `P07-M01-T002` Center Retest; `P07-M01-T003` setup fixture suite.

### P07-M02 — Setup evidence and lifecycle input

- Goal: turn Lane A observations into versioned evidence suitable for lifecycle processing.
- Dependencies: P07-M01.
- Implement: evidence schema, invalidation reasons, and causal input snapshots.
- Acceptance: evidence is traceable to input sequence and cannot imply an order by itself.
- Validation: serialization, replay, and no-lookahead checks.
- Tasks: `P07-M02-T001` evidence schema; `P07-M02-T002` lifecycle input fixtures.

## Phase 08 — Setup Lifecycle and Decision Engine

### P08-M01 — Idempotent lifecycle with explicit decision outcomes

- Goal: run setups through lifecycle states and produce ALLOW/BLOCK/SHADOW decisions.
- Dependencies: P07-M02.
- Implement: state machine, stable setup identity, duplicate/out-of-order handling, and Decision Engine boundary.
- Acceptance: the same event twice cannot create duplicate entry; transitions are deterministic and terminal states are explicit.
- Validation: state-machine, idempotency, replay, and failure-injection tests.
- Tasks: `P08-M01-T001` lifecycle state machine; `P08-M01-T002` Decision Engine; `P08-M01-T003` idempotency/recovery tests.

### P08-M02 — Decision persistence and recovery

- Goal: persist decision reasons and recover lifecycle state without semantic drift.
- Dependencies: P08-M01.
- Implement: decision projections, reason codes, replay checkpoints, and late-event handling.
- Acceptance: a restart preserves identity, causal position, reason codes, and ALLOW/BLOCK/SHADOW outcome.
- Validation: restart, late-event, duplicate, and deterministic replay tests.
- Tasks: `P08-M02-T001` decision projection; `P08-M02-T002` reason-code contract; `P08-M02-T003` lifecycle recovery fixtures.

## Phase 09 — Replay and OHLCV Backtest

### P09-M01 — Research simulation harness

- Goal: measure Lane A over historical OHLCV with deterministic simulated outcomes.
- Dependencies: P08-M02.
- Implement: replay runner, no-lookahead clock, simulated risk/TP/SL, time exits, fees/slippage/fill assumptions, and result journal.
- Acceptance: identical events produce identical decisions and results; limitations of OHLCV microstructure are explicit.
- Validation: determinism, backtest, replay, and failure-injection tests.
- Tasks: `P09-M01-T001` replay runner; `P09-M01-T002` simulated risk/TP/SL; `P09-M01-T003` backtest regression suite.

### P09-M02 — Shared risk math and separate fill simulation

- Goal: prove that research simulation uses the same risk semantics without allowing fill assumptions to authorize orders.
- Dependencies: P09-M01.
- Implement: versioned risk-math adapter, separate fill model, and parity report.
- Acceptance: risk outputs match the shared contract across replay and planning; fill model assumptions are isolated, declared, and replaceable.
- Validation: parity, determinism, and side-effect boundary tests.
- Tasks: `P09-M02-T001` shared risk-math parity; `P09-M02-T002` fill simulation isolation; `P09-M02-T003` assumption report.

### P09-M03 — Frozen research input preparation

- Goal: prepare evaluation inputs before seeing out-of-sample results.
- Dependencies: P09-M02.
- Implement: frozen dataset/config identifiers and predeclared promotion questions without invented thresholds.
- Acceptance: OOS inputs cannot be changed by the result report; unresolved criteria are explicit blockers.
- Validation: manifest immutability and replay reproducibility checks.
- Tasks: `P09-M03-T001` frozen OOS manifest; `P09-M03-T002` predeclared criteria record.

## Phase 10 — Lane B

### P10-M01 — Derivatives behavior confirmation

- Goal: add OI, funding, liquidations, and failed-continuation evidence.
- Dependencies: P09-M03.
- Implement: Lane B features, freshness windows, setup-specific requirements, and shadow evidence.
- Acceptance: stale or missing derivatives data is explicit and cannot silently become confirmation.
- Validation: fixture, stale-data, determinism, and replay tests.
- Tasks: `P10-M01-T001` OI/funding inputs; `P10-M01-T002` liquidation/behavior evidence; `P10-M01-T003` freshness and replay tests.

### P10-M02 — Lane B quality and decision handoff

- Goal: expose derivatives quality and hand off only versioned evidence to the decision path.
- Dependencies: P10-M01.
- Implement: quality flags, causal joins, and shadow-only handoff records.
- Acceptance: missing/stale provider state is visible in the decision trace and cannot bypass lifecycle or risk.
- Validation: quality, join-order, replay, and failure-injection checks.
- Tasks: `P10-M02-T001` derivatives quality state; `P10-M02-T002` causal evidence join; `P10-M02-T003` Lane B handoff fixtures.

## Phase 11 — Risk Engine

### P11-M01 — Independent risk authorization

- Goal: turn decisions into risk decisions with explicit limits.
- Dependencies: P10-M02.
- Implement: sizing, exposure, daily/portfolio limits, kill switch, stops, take profit, cooldown, and fail-closed behavior.
- Acceptance: risk cannot be bypassed by strategy; unknown/stale state blocks authorization; thresholds remain configuration evidence, not invented constants.
- Validation: unit, property, failure-injection, daily/portfolio-limit, and kill-switch tests.
- Tasks: `P11-M01-T001` sizing/limits; `P11-M01-T002` protective levels; `P11-M01-T003` fail-closed risk tests.

### P11-M02 — Portfolio reservations and manual-state handling

- Goal: connect risk authorization to atomic capacity and explicit unknown/manual position states.
- Dependencies: P11-M01.
- Implement: reservation lifecycle, partial-fill protection, manual-position detection, and reconciliation blocks.
- Acceptance: reservations are atomic; partial fills cannot over-release capacity; unknown/manual positions block new entries until reconciled.
- Validation: concurrency, restart, partial-fill, manual-state, and recovery tests.
- Tasks: `P11-M02-T001` reservation integration; `P11-M02-T002` partial-fill protection; `P11-M02-T003` unknown/manual-position fixtures.

## Phase 12 — Private API and Execution

### P12-M01 — Idempotent order lifecycle and reconciliation

- Goal: support private exchange state without enabling live money.
- Dependencies: P11-M02.
- Implement: order/fill/position models, private streams, reconciliation, unknown order recovery, native stops, partial fills, and reduce-only exits.
- Acceptance: accepted, rejected, partial, cancel, timeout, and unknown outcomes converge safely after restart.
- Validation: execution, idempotency, reconciliation, recovery, and failure-injection tests.
- Tasks: `P12-M01-T001` private adapter; `P12-M01-T002` execution state machine; `P12-M01-T003` reconciliation/recovery tests.

### P12-M02 — Private execution development gate

- Goal: verify private execution in controlled development/test paths without conflating it with real-money activation.
- Dependencies: P12-M01.
- Implement: durable intent-before-side-effect enforcement, test adapter contracts, protective-order behavior, and activation handoff evidence.
- Acceptance: private execution can be developed and tested without a live activation path; real-money enablement requires later gate evidence and an explicit user command.
- Validation: test-adapter, idempotency, recovery, and gate-separation checks.
- Tasks: `P12-M02-T001` private development harness; `P12-M02-T002` protective/reduce-only checks; `P12-M02-T003` activation-separation evidence.

## Phase 13 — Lane C

### P13-M01 — Shadow microstructure lane

- Goal: add orderflow, L2, heatmap, absorption, walls, replenishment, and withdrawal evidence in shadow mode.
- Dependencies: P04-M03-T003 and P06-M02-T003; this lane deliberately does not depend on P12 private execution.
- Implement: history capture, features, veto/confirmation outputs, freshness, and sequence checks.
- Acceptance: Lane C cannot place orders and all thresholds remain dataset-driven; capture can run before private execution.
- Validation: orderbook, flow, stale-data, shadow, and determinism tests.
- Tasks: `P13-M01-T001` L2/trade history; `P13-M01-T002` microstructure features; `P13-M01-T003` shadow/veto tests.

### P13-M02 — Lane C quality and research evidence

- Goal: make microstructure data quality and bounded-capture limitations visible to replay and promotion review.
- Dependencies: P13-M01.
- Implement: quality reports, provider/gap exclusions, shadow reason codes, and bounded retention evidence.
- Acceptance: quality failures veto or mark evidence as unavailable; no private execution dependency is introduced.
- Validation: quality, retention, replay, and shadow checks.
- Tasks: `P13-M02-T001` microstructure quality report; `P13-M02-T002` capture exclusion ledger; `P13-M02-T003` Lane C evidence fixtures.

## Phase 14 — Full Historical Replay

### P14-M01 — Multi-source replay dataset

- Goal: replay aligned L2, trades, OI, funding, and liquidation data.
- Dependencies: P13-M02 and P10-M02.
- Implement: dataset manifests, alignment, missing-data policy, and deterministic multi-lane runner.
- Acceptance: manifests are immutable/versioned and reproduce complete decisions with causal ordering and quality status.
- Validation: full replay, determinism, gap, and no-lookahead tests.
- Tasks: `P14-M01-T001` dataset manifest; `P14-M01-T002` aligned replay; `P14-M01-T003` end-to-end regression.

### P14-M02 — Promotion evidence and frozen OOS gate

- Goal: evaluate frozen out-of-sample evidence against criteria declared before results.
- Dependencies: P14-M01.
- Implement: OOS report, data-quality/reconciliation evidence, promotion decision record, and unresolved-blocker handling.
- Acceptance: inputs, configuration, exclusions, and criteria are frozen; no invented numeric threshold appears; a changed model invalidates affected evidence.
- Validation: manifest immutability, report reproducibility, and gate-review fixtures.
- Tasks: `P14-M02-T001` frozen OOS evaluation; `P14-M02-T002` promotion evidence bundle; `P14-M02-T003` gate blocker record.

## Phase 15 — 24/7 Shadow VPS

### P15-M01 — Operational shadow runtime

- Goal: run real market data and hypothetical decisions continuously with zero real orders.
- Dependencies: P14-M02.
- Implement: systemd service, health/reconnect loops, structured logs, metrics, signal/risk/order simulation, and restart recovery.
- Acceptance: restarts recover state; stale data and kill switch stop hypothetical entries; real order count stays zero.
- Validation: shadow soak, restart, reconnect, disk/SQLite, and observability checks.
- Tasks: `P15-M01-T001` service/runtime; `P15-M01-T002` health/metrics; `P15-M01-T003` shadow soak and recovery.

### P15-M02 — Shadow data-quality and gate observability

- Goal: expose operational quality, reconciliation, and zero-order evidence needed for paper review.
- Dependencies: P15-M01.
- Implement: dashboards/log fields, alert reasons, quality snapshots, and audit export.
- Acceptance: stale/gap/reconciliation/gate state is observable and retained with the shadow run.
- Validation: observability, alert, restart, and audit checks.
- Tasks: `P15-M02-T001` quality health signals; `P15-M02-T002` shadow audit evidence.

## Phase 16 — Paper/Test Environment

### P16-M01 — Local paper simulation

- Goal: exercise the shared risk path and separate fill simulation locally.
- Dependencies: P15-M02.
- Implement: local paper configuration, simulated/live-like fills, reconciliation, and reports without exchange credentials.
- Acceptance: paper simulation cannot activate real orders; shared risk math and fill assumptions are separately reported; restart recovery works.
- Validation: paper, execution, recovery, and determinism checks.
- Tasks: `P16-M01-T001` paper configuration; `P16-M01-T002` paper executor; `P16-M01-T003` paper evidence.

### P16-M02 — Exchange test environment

- Goal: verify the private lifecycle against an exchange test environment as a separate gate.
- Dependencies: P16-M01.
- Implement: test credentials/configuration, test exchange adapter, private streams, reconciliation, and reports.
- Acceptance: only test-environment credentials are accepted; all lifecycle cases converge; evidence is kept separate from local simulation and live activation.
- Validation: test-environment, idempotency, recovery, and secret-scan checks.
- Tasks: `P16-M02-T001` test-environment configuration; `P16-M02-T002` test executor; `P16-M02-T003` test-environment evidence.

## Phase 17 — Limited Live

### P17-M01 — Constrained live gate

- Goal: prepare a bounded limited-live activation only after evidence review, without inventing a numeric risk threshold.
- Dependencies: P16-M02.
- Implement: documented seed configuration, one-position/isolation controls where approved, kill switch, manual escalation, and audit trail.
- Acceptance: the seed and any limits are evidence-backed and explicitly approved; no default percentage or automatic expansion is inserted; safeguards and rollback are verified.
- Validation: gate review, recovery, risk-limit, and incident-drill checks.
- Tasks: `P17-M01-T001` limited-live configuration; `P17-M01-T002` live guardrails; `P17-M01-T003` operational gate evidence.

### P17-M02 — Explicit real-money activation command

- Goal: keep activation as a human-controlled, auditable transition.
- Dependencies: P17-M01.
- Implement: activation command contract, preflight checklist, approval record, and immediate disable/rollback path.
- Acceptance: no scheduled job, test result, or service restart can activate real orders; only the documented explicit user command can pass the final gate.
- Validation: negative activation tests, audit, rollback, and fail-closed checks.
- Tasks: `P17-M02-T001` activation command; `P17-M02-T002` preflight/approval record; `P17-M02-T003` rollback and negative-path fixtures.

## Phase 18 — Production

### P18-M01 — Production operations and continuous validation

- Goal: operate the system with evidence-backed limits and maintenance.
- Dependencies: P17-M02.
- Implement: production runbook, alerts, backups, upgrades, incident recovery, and dataset feedback loop.
- Acceptance: production checks, rollback, observability, and periodic causal replay validation are documented and exercised.
- Validation: release, recovery, persistence, replay, and operational acceptance suites.
- Tasks: `P18-M01-T001` production service; `P18-M01-T002` maintenance/alerts; `P18-M01-T003` release acceptance.

### P18-M02 — Continuous promotion and evidence maintenance

- Goal: keep frozen evaluation, data quality, and activation evidence current without silently changing gates.
- Dependencies: P18-M01.
- Implement: evidence expiry/review records, replay refresh policy, and change-to-gate invalidation rules.
- Acceptance: changes to data, risk math, fill assumptions, or execution invalidate affected evidence and require the relevant gate again.
- Validation: evidence invalidation and recovery drills.
- Tasks: `P18-M02-T001` evidence maintenance; `P18-M02-T002` continuous validation.
