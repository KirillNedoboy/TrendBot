# TradingBot project specification

This document is the indexed product/system specification for the greenfield TradingBot. The verbatim supplied technical specification is preserved in [`sources/FINAL_TECH_SPEC.md`](sources/FINAL_TECH_SPEC.md). If a later source conflicts with that document, the final technical specification wins and the conflict must be recorded here or in an ADR.

## Goals

Build a C#/.NET 10 modular monolith for autonomous, deterministic, multi-lane intraday trading research and eventual execution on Binance USD-M perpetual futures. The system must support one Core decision path for replay, backtest, shadow, paper, limited-live, and production modes, with explicit gates before real orders.

## Non-goals for Phase 0–1

The original bootstrap contained no domain contracts, strategy logic, Binance calls, market-data ingestion, Lane A/B/C behavior, Risk Engine, order execution, credentials, or production deployment. Phase 2 now defines passive BCL-only Core domain contracts; behavior and integration for the other items remain future work. Docker, Kafka, Kubernetes, ML-before-dataset, and a third-party Binance runtime SDK remain outside V1.

## Stable requirements

| ID | Requirement |
|---|---|
| REQ-ARCH-001 | Use C# on .NET 10 LTS with a modular-monolith layout. |
| REQ-ARCH-002 | Keep `TradingBot.Core` BCL-only and deterministic. |
| REQ-ARCH-003 | Keep project dependencies acyclic: Exchange, Infrastructure, and Backtesting depend on Core; App composes them; DataTool depends on Infrastructure and Backtesting. |
| REQ-ARCH-004 | Use centralized package versions, lock files, nullable reference types, analyzers, deterministic builds, and warnings as errors. |
| REQ-MARKET-001 | Initial venue is Binance USD-M perpetual futures. |
| REQ-MARKET-002 | Own a thin REST/WebSocket adapter; do not make a Binance SDK a Core runtime dependency. |
| REQ-MARKET-003 | Before implementation, verify the current official Binance Algo Service surface, including the `ALGO_UPDATE` user-data event, endpoint URL, fields, authentication, rate weight, errors, and freshness behavior. |
| REQ-EVENT-001 | Process immutable, timestamped, deterministic event streams with explicit duplicate, ordering, gap, and recovery behavior. |
| REQ-EVENT-002 | Preserve event time, exchange time when supplied, receive/availability time, and a stable processing sequence as distinct causal fields; replay must order equal-time events by the recorded stable sequence without lookahead. |
| REQ-DATA-001 | Use SQLite for operational state/journal and Parquet for high-frequency/research datasets. |
| REQ-DATA-002 | Capture a bounded, versioned research slice early enough to test Lane C assumptions before live activation, with explicit retention, provenance, gaps, and exclusions. |
| REQ-DATA-003 | Emit data-quality observability for freshness, sequence gaps, duplicates, missing fields, clock skew, coverage, and stale-provider state; quality failures remain visible to replay and runtime gates. |
| REQ-STRAT-001 | Target intraday/scalping behavior: HTF context, local setup, and realtime confirmation. |
| REQ-STRAT-002 | Stage Lane A price/structure, Lane B derivatives/behavior, and Lane C microstructure/research. |
| REQ-DECISION-001 | Introduce a distinct Decision Engine in Phase 8 with ALLOW/BLOCK/SHADOW outcomes. |
| REQ-RISK-001 | Keep the Risk Engine independent from strategy and execution; enforce exposure, sizing, stops, and kill switches. |
| REQ-RISK-002 | Use one deterministic risk-math contract across replay, backtest, shadow, paper, and execution planning; fill simulation remains a separate research component with explicit assumptions. |
| REQ-REPLAY-001 | Before the full Risk Engine, Phase 9 must provide deterministic simulated risk, TP/SL, time-exit, and fill assumptions for replay/backtest. |
| REQ-REPLAY-002 | Replays must be causally ordered by event time, receive/availability time, and stable processing sequence, with the ordering tuple persisted in every replay envelope. |
| REQ-EXEC-001 | Add private API, idempotent execution, reconciliation, and protective orders only after shadow/paper gates. |
| REQ-EXEC-002 | Persist order intents and execution outcomes durably before side effects; reconcile open orders, fills, and positions after restart or unknown responses before retrying. |
| REQ-EXEC-003 | Reserve portfolio capacity atomically, protect partial fills with reduce-only logic, and make unknown/manual positions explicit blocks requiring reconciliation. |
| REQ-EXEC-004 | Keep private execution development and test-environment operation separate from the real-money activation gate; no real order may activate without the documented gate and an explicit user command. |
| REQ-OPS-001 | Target a Linux VPS managed by systemd. |
| REQ-OPS-002 | Emit structured JSON logs to journald and expose operational health/metrics in later phases. |
| REQ-OPS-003 | Make data-quality, reconciliation, gate, and zero-real-order signals observable in operational health and audit evidence. |
| REQ-CI-001 | Run restore, format, build, tests, and documentation validation on Windows and Linux GitHub Actions. |
| REQ-SEC-001 | Keep secrets out of source, docs, logs, fixtures, and Git; use environment/secret stores. |
| REQ-VAL-001 | Require deterministic replay regression: identical historical events produce identical setup IDs, states, decisions, risk results, and execution plans. |
| REQ-VAL-002 | Freeze out-of-sample inputs and predeclare promotion criteria before evaluating a strategy; criteria remain evidence-backed and do not invent numeric thresholds. |
| REQ-BOOT-001 | Phase 0–1 creates project memory, architecture decisions, the .NET solution scaffold, tests, and CI without product behavior. |
| REQ-DOC-001 | Every implementation task has a stable ID, dependencies, acceptance criteria, validation, and a current handoff. |
| REQ-GIT-001 | Keep local Git on `main`, ignore `graft/`, and do not create or push a remote during bootstrap. |

## Planning invariants

- The canonical source is immutable. Amendments to it are recorded as explicit working-document requirements or ADR decisions.
- A replay envelope carries event time, exchange time if available, receive/availability time, and a monotonic stable processing sequence. Causal order is deterministic even when timestamps tie or messages arrive late; receive time is never silently substituted for event time.
- Risk math is shared across research and runtime paths. Local paper simulation may model fills separately, with declared assumptions and limitations; a fill simulator cannot authorize private or real orders.
- Private execution development, local paper simulation, and an exchange test environment are separate verification lanes. Real-money activation is a later gate requiring evidence, manual review, and an explicit user command.
- Order intents, reservations, fills, positions, and reconciliation checkpoints are durable. Capacity reservations are atomic; partial fills are protected; unknown exchange state and manual positions remain explicit fail-closed conditions until reconciled.
- Binance private API tasks must re-check current official documentation, including the Algo Service and `ALGO_UPDATE`, immediately before implementation. Unknown parameters and missing external sources remain documented constraints.
- Bounded Lane C/research capture and data-quality observability start before private execution and do not depend on it. Missing, stale, duplicate, or gapped inputs remain visible to every affected gate.
- Out-of-sample datasets and promotion criteria are frozen and declared before evaluation. No undocumented or invented trading threshold is a completion criterion.

## Functional direction after bootstrap

The intended signal path is universe selection → HTF market context → local ATR/channel structure → Lane A setup → setup lifecycle → Lane B derivatives context → Lane C confirmation/veto/research → Decision Engine → shared Risk Engine math → execution plan → exchange execution → position management → replay/research. The initial V1 setups are Boundary Rejection and Center Retest; older setup families remain separate experiments.

Lane C capture is an evidence/research path that can run from public market data and bounded local datasets before private execution exists. It may confirm, veto, or remain shadow-only, but it cannot place orders. Local paper simulation and exchange test-environment execution are separate gates with separate evidence.

## Constraints and quality

- Core stores Price, Quantity, and signed Money in decimal without implicit rounding. Price is positive, Quantity is non-negative, and each Money carries a currency identifier; exchange tick/step quantization is deferred to venue metadata work.
- Event time, exchange time, receive/availability time, and monotonic ordering must not be conflated.
- No lookahead, survivorship bias, selection bias, or unvalidated parameter tuning is acceptable in research.
- Unknown parameters stay unknown until evidence exists: ATR period, channel multiplier, MA lengths beyond research seeds, HTF combination, holding time, Lane B thresholds, Lane C veto thresholds, FDV/token-age/social providers, exact POC window, promotion thresholds, and production risk above limited-live.
- Official current Binance documentation and current API catalogs outrank examples, legacy code, and reference implementations. Each endpoint task must verify URL, fields, rate weight, auth, errors, and freshness. The current Algo Service and `ALGO_UPDATE` contract are part of that verification.
- A task is not made complete by a profitable result, a guessed threshold, a live order, or a remote CI claim that has not run. Local validation and remote GitHub CI status are reported separately.

## External references

The source spec records the supplied Binance, Microsoft/.NET, xUnit, research, and reference implementation links. NautilusTrader is an architecture reference only. Some user-provided videos/notes have no URL, and provider choices for FDV, token age, and social data are absent; these are explicit future-task constraints.

## Completion criteria

The final product is complete only when deterministic causal replay, persistence, all staged data lanes, independent decision/risk/execution boundaries, durable intents and reconciliation/recovery, shadow and separate paper/test evidence, limited-live gates, observability, and production operations are verified by the applicable tests and datasets. Bootstrap completion means only that Phase 0–1 documents and scaffold pass local checks; the remote CI run remains unverified until a repository exists.
