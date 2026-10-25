# Current checkpoint

- Phase: 14 — Full Historical Replay
- Milestone: P14-M02 — Promotion evidence and frozen OOS gate
- Current task: P14-M02-T001 (DONE; frozen OOS evaluation)
- Next task: P14-M02-T002 (READY; implementation stops before this task)

## P14-M01-T001 evidence

- Startup: the required documents were reread, Git was clean, and the docs
  validator selected `P14-M01-T001`; both dependencies were DONE.
- TDD RED: the focused Release Infrastructure.Tests DLL discovered one
  `MultiSourceDatasetManifestTests` case and failed at runtime because the Core
  manifest contract was absent.
- GREEN: Core now defines format-one source, causal ordering, and dataset
  manifest contracts with explicit lane/coverage/quality, source-local and
  dataset-wide sequence identities, unknown-state retention, reference/bounds
  validation, and canonical SHA-256 identity. Infrastructure atomically
  persists and reopens manifests and rejects changed content.
- Acceptance: `MultiSourceDatasetManifestTests` 6/6,
  `CaptureDatasetManifestTests` 7/7, `LaneCCaptureExclusionTests` 6/6,
  `CoreBoundaryTests` 2/2, and `ArchitectureTests` 1/1 passed. Core.Tests and
  Infrastructure.Tests Release warnings-as-errors builds and scoped format
  verification passed. `tools/validate-docs.ps1` and `git diff --check` passed.
- Scope: no packages, schema migration, execution behavior, canonical source,
  `graft/`, commit, or push. `P14-M01-T002` is READY and must repeat preflight
  before implementation.

## P14-M01-T002 evidence

- Startup: the required documents, Git status, dependencies, and documentation
  validator were rechecked; `P14-M01-T002` was READY after T001.
- TDD RED: the focused Release Replay.Tests DLL discovered one
  `MultiSourceReplayTests` case and failed at runtime because the shared runner
  was absent.
- GREEN: Backtesting adds typed records tied to manifest causal references and
  a shared replay clock across Core market events, capture rows/diagnostics,
  and independent Lane B liquidation observations. Delivery uses availability,
  event time, persisted global sequence, and bar close. Each callback receives
  one cursor and an immutable causal prefix. Missing rows and conflicting
  payloads fail before delivery; exact duplicates are recorded and delivered
  once. Existing single-stream overloads remain unchanged.
- Acceptance: `MultiSourceReplayTests` 6/6,
  `HistoricalReplayRunnerTests` 4/4, `LaneBReplayTests` 4/4,
  `BacktestRegressionTests` 5/5, and the affected Replay.Tests Release build
  with `--warnaserror` passed. T001 manifest profile was rerun 6/6 after the
  shared evidence-reference field was added. Scoped format verification,
  documentation validation, and `git diff --check` passed.
- Scope: no package, persistence schema change, live transport, order,
  canonical source, `graft/`, commit, or push. `P14-M01-T003` is DONE after its
  acceptance passed; see the evidence below.

## P14-M01-T003 evidence

- Startup: all five required documents, Git status, task dependencies, SDK, and
  `tools/validate-docs.ps1` were checked before this task; T002 was DONE and
  T003 was selected READY. The canonical source and `graft/` remain untouched.
- TDD RED: the focused Release IntegrationTests DLL built and discovered one
  `MultiLaneDecisionTraceRegressionTests` case, then failed at runtime because
  `MultiSourceReplayResult.ReadinessReasons` was absent.
- GREEN: Backtesting results now expose deterministic readiness reasons for
  incomplete source coverage and non-ready source quality. Replay remains
  available for research, while `DecisionReadinessConfirmed` requires successful
  replay and complete/ready metadata for all included sources.
- Regression fixture: IntegrationTests persists and reopens four capture rows
  and the multi-source manifest, then replays capture and funding lanes and runs
  Lane C features/shadow, Lane B evaluation, Lane A setup evidence, lifecycle
  transitions, Decision, risk evaluation, research plan, and fill simulation.
  Identical Run A/Run B canonical traces match; changing funding rate changes
  Lane B identity and Decision outcome. Partial/degraded capture coverage keeps
  its reasons and blocks the Lane C decision evidence even though the synthetic
  feature values are individually ready. Numeric fixture inputs are test-only.
- GREEN and acceptance: `MultiLaneDecisionTraceRegressionTests` passed 3/3;
  `MultiSourceDatasetManifestTests` 6/6, `MultiSourceReplayTests` 6/6,
  `LaneCEvidenceReplayTests` 2/2, and `ArchitectureTests` 1/1 passed. Sequential
  Core, Infrastructure, Backtesting, App, Infrastructure.Tests, Replay.Tests,
  and IntegrationTests Release builds with `--warnaserror` passed with zero
  warnings/errors. Scoped format verification, `tools/validate-docs.ps1`, and
  `git diff --check` passed. Validator selects `P14-M02-T001` READY.
- No package, schema migration, project-graph change, production activation,
  commit, or push was made. Implementation stops before `P14-M02-T001`.

## P14-M02-T001 evidence

- Startup: reread `AGENTS.md`, `docs/PROJECT_SPEC.md`,
  `docs/EXECUTION_PLAN.md`, `docs/TASKS.md`, and this handoff; Git was clean,
  SDK `10.0.401` matched `global.json`, and the docs validator selected only
  `P14-M02-T001` READY with `P14-M01-T003` DONE.
- TDD RED: the Release Replay.Tests build completed with zero warnings/errors;
  direct execution of `TradingBot.Replay.Tests.FrozenOosEvaluationTests`
  discovered 6 tests and failed all 6 at runtime with `NotImplementedException`
  from the compile-only evaluation definition shell. RED did not use compile
  failure or zero discovery.
- GREEN: Backtesting adds immutable model-version and criteria-evidence
  contracts, a pre-replay definition that retains the old frozen manifest and
  criteria IDs while hashing typed multi-source payloads with all causal
  ordering fields, and a deterministic report/evaluator. Changed research,
  criteria, multi-source manifest, payload, configuration, or model identity
  invalidates the prior record before trace projection. Unknown model versions,
  missing quality references, degraded/unknown coverage, unresolved criteria,
  and absent/unacknowledged reconciliation stay explicit; each delivered trace
  row retains event, exchange, receive, availability, and source-sequence data.
  Reports retain frozen exclusions and exact-duplicate replay diagnostics.
  `Evaluated` records an assessment only, not promotion authority.
- Acceptance: focused `FrozenOosEvaluationTests` passed 11/11;
  `ResearchFreezeTests` 4/4, `PromotionCriteriaTests` 3/3,
  `MultiSourceReplayTests` 6/6, Replay `ArchitectureTests` 1/1,
  `MultiLaneDecisionTraceRegressionTests` 3/3, and
  `MultiSourceDatasetManifestTests` 6/6 passed. Backtesting/Replay.Tests,
  IntegrationTests, and Infrastructure.Tests Release builds with
  `--warnaserror` passed with zero warnings/errors. Scoped format verification,
  `tools/validate-docs.ps1`, and `git diff --check` passed.
- Limits and scope: only synthetic OOS fixtures are present. Real OOS
  performance, shadow/paper, or reconciliation evidence remains unknown and is
  not inferred. No package, project graph, canonical source, `graft/`, order,
  production activation, commit, or push was changed. Validator selects
  `P14-M02-T002` READY; implementation stops before T002.

## Confirmed state

- Core remains BCL-only. Bar validates positive time span and OHLC bounds; Setup requires its anchor price to lie within the recorded high/low range; Position rejects update times before opening.
- `MarketEventComparison.Compare(previous, candidate)` is a pure diagnostic. Event identity is concrete type + InstrumentId + EventId; identical values yield Duplicate and different values with the same identity yield IdentityConflict. Other comparisons distinguish instrument and EventTime; equal EventTime remains SameEventTime without an EventId tie-breaker.
- `CausalMarketEvent` carries the immutable payload, local receive/availability timestamps, optional independent exchange timestamp, and non-negative stable sequence. Availability cannot precede receive. Sequence assignment remains caller-owned and stream-scoped.
- `CausalEventCursor` applies inclusive payload event-time, availability-time, and stable-sequence bounds. `CausalMarketEventOrdering.Compare` orders eligible events by payload EventTime and stable sequence; equal keys do not imply duplicate payloads. Exchange time remains unrelated to local bounds, and receive time is never substituted for event time.
- `CoreJsonSerializer` provides deterministic compact camelCase JSON for current Core contracts with strict numbers, string enums, rejected unknown members, required constructor parameters, `JsonException` for malformed/domain-invalid input, ordered immutable-list conversion, and `$type` discriminators `trade`, `bar`, `bookDelta`, `fundingUpdate`, and `oiUpdate` for `MarketEvent` values.
- `LaneCHistoryCapturePipeline` in App stores recovered public depth snapshots, depth updates, trades, and adapter/recovery diagnostics through the existing bounded Parquet capture store. It preserves depth `U`/`u`/`pu`, recovery generation, per-session source sequence, snapshot REST event/transaction times, and a global capture order; depth fails closed after a recovery gap until a ready snapshot is recorded. Details are in [`docs/architecture/lane-c-history.md`](docs/architecture/lane-c-history.md).
- `LaneCFeatureEvaluator` in BCL-only Core reconstructs captured depth to a causal cursor and computes versioned spread, top-N depth, imbalance, trade flow, per-level changes, and explicit wall/absorption candidates. Window, freshness, size bounds, and caller policy are explicit; absent criteria remain unknown. Details are in [`docs/architecture/lane-c-features.md`](docs/architecture/lane-c-features.md).
- `LaneCShadowEvaluator` applies an explicit versioned caller rule table to versioned feature evidence and returns traceable Confirm/Neutral/Veto/InsufficientData results. Veto has priority; missing/unready evidence or an empty rule table is insufficient data. `DecisionEvidence.FromLaneCShadow` preserves identity/cursor/reasons and maps InsufficientData to existing Unknown and Veto to Veto; the Decision Engine can block required unknown Lane C evidence or return Shadow without order/execution effects. No numeric default rules are defined. Details are in [`docs/architecture/lane-c-shadow-decision.md`](docs/architecture/lane-c-shadow-decision.md).
- `LaneCQualityEvaluator` assesses one instrument's Lane C feature input at its exact cursor; the report keeps those causal statuses separate from full-manifest capture counters. It normalizes known diagnostic codes, records unknown codes with safe source/stream/sequence/time provenance, and fails closed for stale, gapped, conflicting, or unavailable required input. `DecisionEvidence.FromLaneCQuality` maps unavailable quality to Unknown and observed failures to Veto. Infrastructure persists the bounded report atomically against the manifest checkpoint, while App composes feature and report builders. Details: [`docs/architecture/lane-c-quality.md`](docs/architecture/lane-c-quality.md).
- `ExchangeEligibilityMetadata` is a venue-neutral immutable metadata contract. It preserves exact decimal filters, official symbol fields, deterministic raw unknown fields, onboarding/delivery times, and separate local receive/availability provenance. `MetadataCandidateFilter.Evaluate` applies only the USDT perpetual/trading metadata gate and returns ordinally ordered candidates with stable exclusion reasons; it consumes no clock, network, feature, ranking, pool-limit, trade, or execution state.
- `ScreenerFeatureInput`, `ScreenerFeatureConfiguration`, and the immutable causal observation contracts define the minimum screener input. `ScreenerCausalSelection` applies the inclusive event-time/availability-time/stable-sequence cursor primitive before ordering observations, and closed bars also require `Bar.CloseTime <= cursor.AsOf`. `ScreenerFeatureCalculator.Calculate` computes only RVOL, price acceleration, ATR expansion, exact quote-volume turnover, spread, and OI anomaly with explicit completeness, caller-supplied TTL freshness, stable quality reasons, warmup, configuration, denominator, input, and overflow states; it uses no thresholds, ranking, persistence, or execution behavior.
- `ScreenerSnapshot` is the immutable Core format-one record for one minimum screener result. It keeps the complete nullable feature configuration/TTL policy, separate `screener-v1` formula version, causal cursor, normalized finite input/provenance observations, and six feature values with completeness/freshness/reasons. Its identity is the lower-case SHA-256 of canonical Core JSON excluding identity and derived properties; no wall clock is included, and equivalent input orderings normalize to the same identity. `SqliteScreenerSnapshotStore` stores the canonical payload with a 32-byte BLOB identity, verifies row metadata, supported versions, canonical JSON, and recomputed identity on reads, and commits idempotent appends before success.
- `StagedSelectionConfiguration`, `StagedSelectionInput`, `StagedSelectionResult`, and the immutable candidate/exclusion evidence contracts define the versioned staged-selection boundary. `UniverseSelectionStage` filters metadata by availability at one shared `CausalEventCursor`, applies the existing metadata-only eligibility gate, collapses exact duplicate screener snapshots, rejects conflicts and future observations, recalculates features from saved inputs, requires every configured Turnover/Spread key to be complete and fresh, and ranks lexicographically with an ordinal InstrumentId tie-break. `CandidatePoolSelectionStage` accepts only the selected Universe membership, carries the same cursor and an explicit prior-stage identity/membership reference, recalculates only the configured six-feature keys, and retains deterministic ranks, reasons, evidence, and capacity exclusions. `RealtimeMonitorSelectionStage` accepts only CandidatePool membership and same-cursor evidence, retains the CandidatePool link, applies the same six-key readiness/ranking rules, and bounds the result at 20. `StagedSelectionChainSnapshot` stores the three stage inputs, evidence, configurations, results, provenance, and exclusions in canonical format-one JSON; it validates the complete Universe→CandidatePool→RealtimeMonitor links and recomputes a lower-case SHA-256 identity without the identity field. Selected and excluded rows retain metadata, snapshot evidence, recalculated values, comparisons, rank, cursor, filter evidence, configuration, and stage provenance; there are no default weights, thresholds, TTLs, persistence, setups, subscriptions, or execution state.
- `RollingIndicatorInput`, `RollingIndicatorConfiguration`, and the immutable rolling result contracts define the BCL-only `rolling-v1` ATR, SMA/EMA/WMA/RMA, and RVOL boundary. `RollingIndicatorCalculator` filters event time, availability, stable sequence, and closed-bar time at one cursor before calculating; explicit `HistoryStart` controls recursive seeds. It uses checked decimal formulas, N/N+1 warmups, duplicate/conflict/gap reasons, zero-denominator and overflow states, canonical ordinal ordering, and retained input/seed provenance. No numeric defaults, TTLs, levels, channels, setups, subscriptions, persistence, or execution were added. Architecture details are recorded in [`docs/architecture/rolling-indicators.md`](docs/architecture/rolling-indicators.md).
- `RiskMathVersion`, `MoneyPerQuantityUnit`, `RiskMathInput`, `RiskMathResult`, and `IRiskMathCalculator` define versioned, deterministic, side-effect-free sizing/exposure contracts shared across replay, paper simulation, and planning. Inputs snapshot the required instrument, direction, equity, risk fraction, entry/stop prices, fee, and slippage. Results include risk budget, loss per unit, raw pre-venue-constraint quantity, and notional exposure. Constructors enforce positive outputs and matching currencies without recomputing formulas; no execution mode, risk decision, sizing implementation, or trading defaults were added. Calculation parity remains P09-M02-T001.
- `FillSimulationVersion`, `FillSimulationAssumptions`, `FillSimulationInput`, `ResearchFill`, `FillSimulationResult`, and `IFillSimulator` define a separate passive research boundary. Inputs are identified hypothetical requests with positive quantity, optional reference price, request time, non-empty fee/slippage/latency/liquidity-queue/partial-fill/intrabar/data-limitation descriptions, and an immutable causal market snapshot. Snapshot events must match the instrument and be available by the request time. Results defensively retain the full input snapshot and only research fills; fill identifiers are unique and total filled quantity cannot exceed the hypothetical request. No fill contract references `OrderIntent`, `RiskDecision`, risk math, executor, or authorization state. Unknown assumptions remain prose, with no numeric defaults. Fill algorithms, replay integration, persistence, and paper execution remain future work.
- `ReplayEnvelope` is an immutable version-1 Core contract carrying its stream ID and complete `CausalMarketEvent`. `SqliteEventJournal` persists the envelope version and reads immutable envelope streams; migration 1 -> 2 assigns version 1 to prior rows. `ReplaySnapshotBuilder` filters with the inclusive causal cursor before ordering by event time and stable sequence. This is a bounded snapshot, not a full replay runner.
- `SqliteOrderIntentJournal` persists the complete Core `OrderIntent` as canonical JSON in the shared SQLite schema. Schema migration 2 -> 3 adds `order_intents` without rewriting `event_journal`; `IntentId` and `ClientOrderId` use BINARY uniqueness. Identical retries return false, identity conflicts roll back, and a successful append returns only after commit. This stage records only an intent awaiting future execution; it adds no adapter, submission, reservation, fill, or reconciliation behavior.
- `PortfolioId`, `PortfolioLimits`, and `PortfolioReservation` are passive immutable BCL-only Core contracts. Limits and reservations require explicit non-negative monetary risk/notional amounts, non-negative position slots, and one currency within each monetary value pair; no trading defaults are supplied.
- `SqliteDatabase` is the shared transactional SQLite initializer. Schema migration 3 -> 4 adds BINARY `portfolios` and `reservations` storage while preserving `event_journal` and `order_intents`; decimal capacity values remain canonical JSON text and are never SQLite REAL values.
- `SqlitePortfolioReservationStore` registers immutable limits, reads persisted limits/reservations and saved risk evidence, and performs authenticated `TryReserve(intent, reservation, riskEvaluation, expectedReconciliationRevision, riskMathCalculator)` in one immediate serializable transaction. It recomputes canonical risk evidence, checks approval and identity parity, rereads checked risk/notional/slot totals, and atomically binds the intent, reservation, and evidence. Results are `Reserved`, `AlreadyReserved`, or `InsufficientCapacity`; conflicts and failures leave no partial mutation.
- `tests/Fixtures/CoreFixtureVectors.cs` remains a linked test-only source in `TradingBot.Core.Tests` and `TradingBot.Replay.Tests`, using only BCL APIs and `TradingBot.Core`.
- [`docs/architecture/core-contract-evidence.md`](docs/architecture/core-contract-evidence.md) records the current fixture identifiers and exact vector matrix. The strings are test examples, not released versions, and no global Core or wire-schema version exists.
- [`docs/architecture/persistence.md`](docs/architecture/persistence.md) records SQLite schema versions 1 through 11, including envelope, intent, portfolio-reservation, operational, capture, screener-snapshot, staged-selection, decision-projection, and risk-evidence migrations, append/read guarantees, bounded segment durability, and evidence limits.
- `SqliteEventJournal`, `SqliteOrderIntentJournal`, `SqlitePortfolioReservationStore`, `SqliteScreenerSnapshotStore`, and `SqliteUniverseSelectionSnapshotStore` share the Infrastructure initializer. They use Microsoft.Data.Sqlite 10.0.12, one non-pooled connection per operation, canonical Core JSON, WAL, `synchronous=FULL`, and BINARY/blob identities. Existing event rows receive envelope format version 1 transactionally; operational observations and checkpoints are durable across restart.
- `ParquetCaptureStore` uses Parquet.Net 6.1.0 plus the same SQLite initializer. One store owns one stream, rejects gaps and bounded-buffer overflow, writes fsynced temporary Parquet files before atomic rename, and commits schema-6 segment metadata/checkpoints transactionally. Reads validate scalar columns against canonical CaptureEnvelope JSON and preserve causal/provenance fields across restart. Schema 6 -> 7 adds screener snapshot rows and schema 7 -> 8 adds staged-selection rows, preserving capture state.
- `CaptureDatasetManifest` is an immutable format-one allowlisted metadata record pinned to one durable capture checkpoint. Its bounded builder reads segments incrementally, records source/instrument/session provenance, event/receive/availability bounds, ordering/configured bounds, and separate gap/exclusion evidence while retaining unknown coverage as `Unknown`. The manifest omits payloads and local paths; atomic JSON persistence rejects a changed checkpoint on reopen.
- `CaptureRetentionPlanner` performs a caller-timed UTC dry-run only. Nullable age/byte policy values remain unknown without defaults; known age uses segment durable time, byte selection orders by durable time then first capture sequence, and every candidate carries reasons. The planner never deletes files or mutates checkpoints.
- `CaptureQualitySnapshot` is an immutable format-one quality record linked to the same manifest/checkpoint. Its bounded builder counts explicit `gap`, `duplicate`, `missing-field`, `clock-skew`, and `stale` evidence once per record, counts unknown diagnostic codes, preserves event-to-receive latency summaries and last receive time, and keeps freshness/provider-silence/clock-skew status `Unknown` when caller thresholds are missing. It does not infer clock skew from latency or gaps from stable sequence.
- `CaptureQualitySnapshotStore` atomically persists/reopens snapshots by manifest identity and checkpoint. `CaptureQualityMetricsPublisher` uses BCL `System.Diagnostics.Metrics` with fixed instrument names, no dynamic identifiers/tags, and an idempotence fingerprint so rereading one snapshot does not double-count counters; observable gauges expose last receive and status codes.
- `Fill`, `PortfolioPositionSnapshot`, and `ReconciliationCheckpoint` are immutable BCL-only Core contracts. Operational observations advance a portfolio revision transactionally, default to `Unreconciled`, preserve unknown/manual markers, and invalidate acknowledgement on every new revision. The retry guard requires acknowledgement and no unresolved markers. Partial fills retain the full reservation; release is idempotent and requires a current acknowledged closed-order/closed-position checkpoint.
- `TradingBot.Exchange.Binance` now owns the public REST contract over injected `HttpClient`. `BinancePublicRestClient` uses current USD-M `/fapi/v1` market-data paths, immutable DTOs, exact decimal parsing, raw Unix-millisecond fields, cancellation, response-size limits, and observable HTTP/API/rate errors. It maps only completed klines to Core `Bar`; recent/aggregate trades, mark/history funding, and current OI map to Core events.
- `ExchangeEligibilityMetadata` is an immutable, venue-neutral Core contract carrying symbol identity, contract/status/assets, onboarding/delivery times, exact decimal filters, official symbol extensions, unknown raw fields, and separate local receive/availability timestamps. `BinanceSymbolFilter` retains `notional`, `multiplierDecimal`, and unknown filter fields/types; exchangeInfo `serverTime` remains wire data and is never used for provenance.
- `docs/architecture/market-data.md` records the 2026-10-12 public endpoint matrix, the 2026-10-16 exchangeInfo/filter facts, weights/auth boundary, DTO/Core mapping decisions, the approved WebSocket `/market` route, and the depth `/public/stream` snapshot/diff contract. T002 owns general market transport; T003 owns depth transport and orderbook recovery.
- `BinanceMarketWebSocketClient` uses an injected transport and `TimeProvider`, parses raw/combined fragmented messages for aggregate trades, klines, tickers, mark-price/funding, and liquidations, and emits immutable session envelopes. Reconnect/resubscribe is bounded and configurable; duplicate, conflict, late, stale, disconnect, gap, parse, and frame-limit diagnostics remain visible. Depth subscriptions are rejected on `/market` and routed through the dedicated depth client.
- `BoundaryRejectionSetupEvaluator` is the pure `boundary-rejection-v1` Lane A family boundary. It consumes a `price-context-v2` result and `feature-quality-v1` evidence at one causal cursor, evaluates LONG/SHORT SETUP-01 conditions on the confirmed channel/local-trend signal bar, compares a caller-supplied positive minimum channel width with the full `Upper - Lower` span, and requires exactly ready and consistent Channel/LocalTrend/HtfContext quality evidence. Ready/NoSetup/Invalidated and explicit unknown states are immutable and serializable; strict close-through is a per-cursor Invalidated observation, equality is NoSetup, and no lifecycle, persistence, risk, order, or execution state is added. Architecture details are recorded in [`docs/architecture/boundary-rejection.md`](docs/architecture/boundary-rejection.md).
- `CenterRetestSetupEvaluator` is the pure `center-retest-v1` Lane A family boundary. It consumes the same `price-context-v2` and `feature-quality-v1` evidence at one causal cursor, evaluates LONG/SHORT SETUP-02 conditions around `PriceContextChannel.Center` on the confirmed signal bar, mirrors the canonical SHORT local-bias condition as `Bear`, and requires exactly ready and consistent Channel/LocalTrend/HtfContext quality evidence. Center Retest has no minimum-width gate and consults no channel width or bands. Ready/NoSetup/Invalidated and explicit unknown states are immutable and serializable; strict close-through is a per-cursor Invalidated observation, equality is NoSetup, and no lifecycle, persistence, risk, order, or execution state is added. Architecture details are recorded in [`docs/architecture/center-retest.md`](docs/architecture/center-retest.md).
- `LaneASetupFixtureTests` is the test-only cross-family Lane A suite. It exercises a 14-case hand-specified matrix across Boundary Rejection and Center Retest, verifies evidence invariants, round-trips price-context/quality snapshots and setup results, replays cursor prefixes through serialized evidence, checks future/no-lookahead behavior, and covers permutation/culture determinism plus V1/execution boundary guards. No production contracts or behavior were added; details are recorded in [`docs/architecture/lane-a-setup-fixtures.md`](docs/architecture/lane-a-setup-fixtures.md).
- `SetupEvidenceSnapshot` is the immutable per-direction `setup-evidence-v1` schema bridging Lane A to Phase 8. It retains the complete `price-context-v2`/`feature-quality-v1` input, one family configuration/observation, a validated causal `SetupEvidenceInputSequence`, and explicit `SetupEvidenceInvalidation` state/ClosedThrough/reasons. It has a lower-case SHA-256 content identity, rejects mismatched or tampered JSON, re-evaluates the selected family in its constructor, and `Replay()` reproduces the identity. It contains no lifecycle, decision, risk, order, or execution state; details are recorded in [`docs/architecture/setup-evidence.md`](docs/architecture/setup-evidence.md).
- The canonical final technical specification was not edited. Graft remains ignored and untouched.

## P13-M01-T003 evidence

- Before T003, the task/dependency status and `pwsh -NoProfile -File tools/validate-docs.ps1` confirmed T003 as the next eligible READY task. TDD RED: focused Core.Tests Release DLL profiles each discovered one test and failed 1/1 at runtime because the Lane C shadow evaluator and `DecisionEvidence.FromLaneCShadow` were absent.
- GREEN: BCL-only versioned rule, result, and metadata contracts plus a pure shadow evaluator preserve input/result identities, rule version, causal cursor, matched rule IDs, and reason codes. Caller tables have no default thresholds; empty/no-evaluable rules or unavailable features yield InsufficientData, and Veto takes priority over Confirm. Decision evidence maps InsufficientData to Unknown and Veto to Veto; required Unknown blocks, while accepted evidence can yield Shadow without order or execution side effects. Details: [`docs/architecture/lane-c-shadow-decision.md`](docs/architecture/lane-c-shadow-decision.md).
- Direct Release DLL profiles passed: LaneCShadowTests 4/4, LaneCDecisionTests 3/3, DecisionEvidenceMetadataTests 5/5, DecisionReasonCodesTests 4/4, DecisionProjectionSnapshotTests 4/4, SetupLifecycleTests 12/12, CoreBoundaryTests 2/2, ArchitectureTests 1/1, and LaneCFeatureTests 8/8. Core.Tests Release build with `--warnaserror` completed with zero warnings/errors; scoped format verification, docs validation, and `git diff --check` passed. Validator now selects P13-M02-T001 READY.
- No full suite, package/project-graph change, canonical source change, `graft/` edit, commit, or push was performed. P13-M01 is complete; stop before P13-M02-T001.

## P13-M02-T001 evidence

- Startup status and the validator selected T001; `P13-M01-T003` was DONE. TDD RED used direct Release xUnit DLL profiles: quality report 2/2 runtime failures because the report builder was absent; quality/Decision 2/2 because evaluator/mapping were absent; App composition 1/1 because the App service was absent. A focused follow-up test for generic conflict counting failed at runtime 1/3 before the counter was implemented.
- GREEN adds immutable versioned Core quality evidence linked to feature input identity and causal cursor, with explicit provider policy, per-category signal counts, safe unknown diagnostic provenance, deterministic identity and Decision mapping. Infrastructure produces bounded manifest/checkpoint reports, verifies durable feature rows and identity links, stores/reopens reports atomically, and separates causal quality from whole-manifest counters. `CaptureQualitySnapshot` now counts known conflicts and reads legacy payloads with absent `conflictCount`; its current freshness uses receives at or before `asOf`. App composes feature and report evaluation. Details: [`docs/architecture/lane-c-quality.md`](docs/architecture/lane-c-quality.md).
- Acceptance passed: LaneCQualityDecisionTests 7/7; LaneCQualityReportTests 3/3; LaneCQualityReportCompositionTests 1/1; CaptureQualitySnapshotTests 4/4; CaptureDatasetManifestTests 7/7; ParquetCaptureStoreTests 4/4; LaneCFeatureTests 8/8; LaneCShadowTests 4/4; LaneCDecisionTests 3/3; DecisionEvidenceMetadataTests 5/5; CoreBoundaryTests 2/2; ArchitectureTests 1/1; LaneCHistoryCaptureTests 3/3. Core.Tests, Infrastructure.Tests, and IntegrationTests Release builds passed with `--warnaserror`, zero warnings/errors. Scoped format, docs validation, and `git diff --check` passed. Validator now selects P13-M02-T002 READY. No full suite, package/project-graph, canonical source, or `graft/` changes.

## P13-M02-T002 evidence

- Preflight rechecked task dependencies and validator after T001; T002 was the first READY task and T001 was DONE. TDD RED: the focused Release Infrastructure.Tests DLL selected 4 `LaneCCaptureExclusionTests` and all 4 failed at runtime because the ledger and safe-reference behavior were absent. Follow-up profile REDs confirmed read-time segment validation failed 1/1 and complete-evidence ordering failed 1/1.
- GREEN adds a versioned Infrastructure ledger linked to quality report identity, manifest id, checkpoint, and unchanged coverage. Exclusion entries retain safe source/instrument scope, stable reason codes, provenance, and nullable known bounds. Builder and readback validate manifest segment/sequence references; complete-evidence ordering and exact-entry deduplication are deterministic. Retention records the existing DryRun result with its nullable policy, as-of time, candidates, and reasons. The atomic JSON store reopens with manifest/report/checkpoint/coverage/reference checks. Candidate retention does not remove data or change checkpoints. Details: [`docs/architecture/lane-c-exclusions.md`](docs/architecture/lane-c-exclusions.md).
- Acceptance passed: `LaneCCaptureExclusionTests` 6/6, `CaptureDatasetManifestTests` 7/7, `LaneCQualityReportTests` 3/3, `CaptureQualitySnapshotTests` 4/4, and `ParquetCaptureStoreTests` 4/4. Infrastructure.Tests Release build with `--warnaserror` passed with zero warnings/errors. Scoped format verification, docs validator, and `git diff --check` passed; validator selects P13-M02-T003 READY.
- No full suite, delete/checkpoint mutation, package/project-graph, canonical source, or `graft/` change. Recheck status, dependencies, Git, and validator before starting T003.

## P13-M02-T003 evidence

- Preflight after T002 rechecked Git status, task dependencies, and the documentation validator; T003 was first READY and T002 was DONE. TDD RED: a direct Release IntegrationTests DLL run selected `LaneCEvidenceReplayTests` and failed 1/1 at runtime because App had no verifiable Lane C evidence bundle builder.
- GREEN adds versioned immutable `LaneCEvidenceBundle` in App, validating manifest/checkpoint, quality report, exclusion ledger, feature, shadow, and Decision evidence identities and causal cursors. The bundle is deterministic canonical evidence only and has no order/execution action. `LaneCEvidenceReplayTests` captures fixed synthetic Binance snapshots, depth updates, and trades, then adds gap/recovery and identity-conflict diagnostics; equal event times retain distinct stable sequences and delayed availability is excluded from the earlier cursor. Capture, quality report, and exclusion ledger are reopened before repeated replay and serialization comparison. Expected results cover recovered state with historical gap, stale cursor, conflict, exclusions, InsufficientData/Unknown decision, stable reasons and unchanged capture checkpoint. Details: [`docs/architecture/lane-c-evidence-replay.md`](docs/architecture/lane-c-evidence-replay.md).
- Acceptance passed: LaneCEvidenceReplayTests 2/2, LaneCHistoryCaptureTests 3/3, LaneCQualityReportCompositionTests 1/1, LaneCFeatureTests 8/8, LaneCShadowTests 4/4, LaneCDecisionTests 3/3, LaneCQualityDecisionTests 7/7, CoreBoundaryTests 2/2, Core ArchitectureTests 1/1, LaneCCaptureExclusionTests 6/6, LaneCQualityReportTests 3/3, CaptureQualitySnapshotTests 4/4, CaptureDatasetManifestTests 7/7, and ParquetCaptureStoreTests 4/4. Core.Tests, Infrastructure.Tests, and IntegrationTests Release builds with `--warnaserror` passed with zero warnings/errors; scoped format verification, docs validator, and `git diff --check` passed. The validator now selects P14-M01-T001 READY.
- Scope: no full suite, order/execution action, capture checkpoint mutation, package/project-graph, canonical source, `graft/`, commit, or push. Stop before P14-M01-T001; repeat status/dependency/validator preflight before that block.

## P11-M01-T001 evidence

- Startup: the PowerShell 7 documentation validator selected `P11-M01-T001` as the first READY task; `P10-M02-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source remains immutable.
- TDD RED: the focused Core.Tests Release build first failed on the missing `RiskEvaluationInput` and `RiskEvaluationConfiguration` contracts. After a compile-only skeleton, direct xUnit execution of `TradingBot.Core.Tests.RiskEngineTests` failed 3/3 for the expected unimplemented behavior.
- GREEN: Core adds immutable `RiskEvaluationInput`, `RiskEvaluationConfiguration`, `RiskEvaluationResult`, explicit health/kill-switch/cooldown/protection evidence records, caller-owned risk limit and portfolio-usage records, stable `RiskReasonCodes`, and pure `RiskEvaluationEngine`. It accepts only a matching `DecisionProjectionSnapshot` whose outcome is `Allow`; validates the calculator version and full `RiskMathInput` snapshot; reuses the raw `RiskMathResult`; checks caller-supplied daily loss for its explicit period plus candidate risk; checks per-trade, portfolio risk, notional, margin, and slot capacities with checked decimal arithmetic; rejects currency and identity mismatches; and never reduces a proposed size. Missing protection/cooldown remains blocked, so T001 cannot produce full approval.
- Verification: direct xUnit `RiskEngineTests` passed 11/11; Core Release build with `--warnaserror` passed with zero warnings/errors; scoped `dotnet format --no-restore --verify-no-changes` and `git diff --check` passed. T002 is now READY and must be reselected before implementation.
- Scope: changes are limited to Core risk evaluation contracts/engine, focused Core tests, `docs/TASKS.md`, this handoff, and the risk-model architecture note. No portfolio reservation integration, execution, package, project-graph, P13, or canonical source change was made; no commit or push was performed.

## P11-M01-T002 evidence

- Startup: after T001, the validator selected `P11-M01-T002` as the first READY task; `P11-M01-T001` was DONE and `P13-M01-T001` remained independently READY. The canonical source remains immutable.
- TDD RED: the focused Core.Tests Release build passed after the T001 implementation, then direct xUnit `RiskEngineProtectionTests` failed 3/10 for the missing aggregate level-link rejection and approval behavior.
- GREEN: `RiskProtectionEvidence` now validates every explicit stop/TP level against aggregate instrument, direction, setup/method link, source/version, freshness, and causal cursor. `RiskEvaluationEngine` validates stop equality with the shared `RiskMathInput`, loss/profit side for LONG/SHORT, fresh causal evidence, and explicit cooldown state. Unknown/stale/future/conflicting/mismatched evidence blocks; active cooldown blocks before its supplied expiry and clear state permits at the boundary. Approval remains a passive `RiskDecision` evidence record.
- Verification: direct xUnit `RiskEngineProtectionTests` passed 10/10; adjacent `RiskEngineTests` passed 11/11; Core Release build with `--warnaserror` passed with zero warnings/errors; scoped `dotnet format --no-restore --verify-no-changes` and `git diff --check` passed. T003 is now READY and must be reselected before implementation.
- Scope: changes are limited to Core risk contracts/engine, focused Core tests, `docs/TASKS.md`, and this handoff. No reservation, execution, package, project-graph, P13, or canonical source change was made; no commit or push was performed.

## P11-M01-T003 evidence

- Startup: after T002, the validator selected `P11-M01-T003` as the first READY task; `P11-M01-T002` was DONE and `P13-M01-T001` remained independently READY. The canonical source remains immutable.
- TDD RED: the focused Core.Tests Release build passed after T002, then direct xUnit `RiskEngineFailClosedTests` first failed 1/7 because a tampered `RiskDecision` identity/version link was accepted by `RiskEvaluationResult` construction; the added calculator-version failure fixture then escaped before its guard.
- GREEN: result construction now validates evaluator/configuration version and deterministic `risk-{EvaluationId}` plus source Decision identity, and rejects retained risk-math output whose version or complete input snapshot differs. The pure engine fail-closes unknown/missing policies and health, stale/future/conflicting/mismatched evidence, enabled/unknown kill switch, currency mismatch, checked-capacity overflow, calculator version/input/exception, and causal lookahead. Kill-switch rejection occurs before calculator invocation; LONG/SHORT side checks, exact limits, strict JSON, and absence of execution/reservation surfaces are covered.
- Verification: direct xUnit `RiskEngineFailClosedTests` passed 8/8; adjacent `RiskEngineTests` passed 11/11 and `RiskEngineProtectionTests` passed 10/10; Core Release build with `--warnaserror` passed with zero warnings/errors; scoped `dotnet format --no-restore --verify-no-changes`, `tools/validate-docs.ps1`, and `git diff --check` passed. Expected next READY tasks are `P11-M02-T001` and `P13-M01-T001`; implementation stopped at P11-M01.
- Scope: changes are limited to Core risk contracts/engine, focused Core tests, `docs/architecture/risk-model.md`, `docs/TASKS.md`, and this handoff. No reservation integration, execution, package, project-graph, P13, or canonical source change was made; no commit or push was performed.

## P06-M02-T001 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FeatureQualityEvaluatorTests"` failed with the expected missing `FeatureQualityEvaluation`, `FeatureQualityEvaluator`, policy, evidence, and enum contracts before implementation.
- `FeatureQualityEvaluator` is an independent BCL-only quality boundary for `rolling-v1` and `price-context-v2`. It preserves calculator formulas and serialized contracts, reads no clock, accepts nullable rolling/setup/HTF/price-volume TTLs without defaults, computes market-data age from bar close/event time and availability age from local availability against the result cursor, and treats equality with TTL as fresh and values above TTL as stale. Missing and non-positive TTLs remain unknown.
- Immutable per-feature evidence retains original state/completeness, source formula version, derived freshness, both ages, TTL, known/required counts, bounded coverage, duplicate/gap/conflict/incomplete reasons, and fail-closed readiness. Exact duplicates are canonicalized once while their diagnostics remain visible; stale, unknown, incomplete, gapped, or conflicting inputs cannot produce ready quality evidence.
- TDD GREEN: focused FeatureQualityEvaluatorTests passed 3/3; affected RollingIndicatorTests and PriceContextFeatureTests passed 25/25; Core.Tests Release build with `--warnaserror` passed with zero warnings/errors; scoped `dotnet format --verify-no-changes` passed after normalizing new files to repository CRLF/encoding. Architecture details are recorded in [`docs/architecture/feature-quality.md`](docs/architecture/feature-quality.md).
- `pwsh -NoProfile -File tools/validate-docs.ps1` is required immediately after this transition; the expected next validator-selected task is `P06-M02-T002`. No persistence, metrics, setup, execution, Phase 7, or Lane C behavior was added.

## Verification evidence

- Startup protocol: worktree was clean at `7c46c1b`; P03-M01-T002 was the first `READY` task and P03-M01-T001 was `DONE`.
- Unlocked restore: `C:\temp\dotnet10\dotnet.exe restore TradingBot.slnx` — exit 0.
- Locked restore: `C:\temp\dotnet10\dotnet.exe restore TradingBot.slnx --locked-mode` — exit 0.
- RED evidence: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SqliteEventJournalTests"` failed before implementation with the expected missing `SqliteEventJournal` compile errors.
- Release Infrastructure.Tests build: `C:\temp\dotnet10\dotnet.exe build tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --warnaserror` — exit 0, zero warnings and errors.
- Infrastructure tests: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-build --no-restore` — 10 passed, 0 failed.
- Core boundary/project-graph build: `C:\temp\dotnet10\dotnet.exe build tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --warnaserror` — exit 0, zero warnings and errors.
- Core boundary/project-graph tests: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~CoreBoundaryTests|FullyQualifiedName~ArchitectureTests|FullyQualifiedName~ProductionProjectGraphMatchesApprovedModuleBoundaries"` — 4 passed, 0 failed.
- Changed C# format: `C:\temp\dotnet10\dotnet.exe format TradingBot.slnx --no-restore --verify-no-changes --include src/TradingBot.Infrastructure/SqliteEventJournal.cs tests/TradingBot.Infrastructure.Tests/SqliteEventJournalTests.cs` — exit 0.
- Documentation validation: `pwsh -NoProfile -File tools/validate-docs.ps1` — exit 0; phases=19, milestones=41, tasks=119, ready=P03-M02-T001.
- P03-M01-T002 locked restore — exit 0; Core focused tests — 3 passed; Infrastructure tests — 12 passed; Replay tests — 5 passed; Release warnings-as-errors builds for Core.Tests, Infrastructure.Tests, and Replay.Tests — zero warnings/errors; scoped C# format — exit 0.
- P03-M01-T002 documentation validation: `pwsh -NoProfile -File tools/validate-docs.ps1` — exit 0; phases=19, milestones=41, tasks=119, ready=P03-M01-T003.
- `git diff --check` — exit 0.
- Canonical source SHA-256: `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- GitHub Actions run [`37671835797`](https://github.com/KirillNedoboy/TrendBot/actions/runs/37671835797) for the preceding commit completed with failure on `Verify formatting` on both Windows and Ubuntu. The CI correction remains outside this task.

## P03-M01-T003 evidence

- IntegrationTests adds a real SQLite write-close-reopen replay proof through SqliteEventJournal and ReplaySnapshotBuilder.
- Restart comparison covers envelopes, stable-sequence storage order, canonical JSON, causal snapshot order, and a deterministic projection string.
- Focused recovery cases cover idempotent repeat, same-key conflict, sequence gaps, late event time, unavailable events, corrupted payloads, write lock timeout, and no partial append after failure.
- Locked IntegrationTests restore passed; Release IntegrationTests build with warnings as errors passed with zero warnings and errors; all 5 IntegrationTests passed; scoped C# format verification passed.

## P03-M02-T001 evidence

- `SqliteOrderIntentJournal` opens the shared file-backed schema, appends canonical `OrderIntent` JSON transactionally, returns false for identical retries, rejects both IntentId and ClientOrderId conflicts without mutation, and finds intents after restart.
- Targeted tests cover all intent fields and decimal/null precision, schema 2 -> 3 migration with event preservation, shared event/intent use, rollback, and a one-second busy-writer failure with no partial row.
- Locked restore passed; Release Infrastructure.Tests build with warnings as errors passed with zero warnings and errors; focused intent tests passed 6/6 and all Infrastructure tests passed 18/18; scoped C# format verification passed.
- Documentation validation passed with first READY task P03-M02-T002; `git diff --check` passed; canonical `FINAL_TECH_SPEC.md` SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P03-M02-T002 evidence

- Core adds immutable `PortfolioId`, `PortfolioLimits`, and `PortfolioReservation` contracts with explicit caller-supplied monetary-risk, notional-exposure, and position-slot values. Matching currencies, non-negative values, strict JSON round trips, and immutability are covered by `PortfolioReservationContractTests`.
- Infrastructure extracts shared SQLite initialization and migration 3 -> 4. The migration creates BINARY `portfolios` and `reservations` tables while preserving existing `event_journal` and `order_intents` rows. `SqlitePortfolioReservationStore` persists canonical JSON only, keeps identity columns BINARY, and writes an existing `OrderIntent` with its reservation atomically.
- `SqlitePortfolioReservationStoreTests` cover exact capacity boundaries, independent-connection restart recovery, duplicate idempotence, portfolio/reservation/intent conflicts, missing portfolios, currency mismatch, rollback under a held writer, and separate barrier races for risk, notional, and position slots. Focused reservation tests passed 11/11; full Infrastructure.Tests passed 29/29.
- Release IntegrationTests and Replay.Tests builds passed with warnings as errors; IntegrationTests passed 5/5 and Replay.Tests passed 5/5. Core.Tests passed 62/62, including the new contract tests; Release Core.Tests and Infrastructure.Tests builds passed with zero warnings/errors; scoped C# format verification passed.
- Locked restore, documentation validation, `git diff --check`, and canonical source hash verification passed. The canonical `FINAL_TECH_SPEC.md` SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P03-M02-T003 evidence

- Core adds immutable `Fill`, `PortfolioPositionSnapshot`, and `ReconciliationCheckpoint` contracts. Fill values retain intent/instrument identity, positive quantity and price, optional fee, event/receive/availability/exchange timestamps, and stable sequence. Snapshots retain Core `Position` values with known intent ownership or explicit manual/unknown origin. Checkpoints retain monotonic revision, open intents/positions, unknown/manual markers, and explicit `Unreconciled`/`Acknowledged` status.
- Infrastructure schema migration 4 -> 5 adds the reservation release marker, fills, immutable position snapshots, and current reconciliation checkpoints while preserving event, intent, portfolio, and reservation rows. `RecordObservation` persists observations and the next checkpoint atomically using checked application decimal arithmetic; duplicate observations are idempotent and conflicts, aggregate overflow, stale revisions, and gaps leave no partial mutation.
- `SqliteOperationalStateTests` cover restart persistence, duplicate idempotence, acknowledgement invalidation, fill identity conflict, aggregate overflow, stale revision rejection, unknown/manual retry blocking, full reservation retention through a partial fill, acknowledged closed-state release, repeated release, and 4 -> 5 migration preservation.
- Locked restore passed. Release warnings-as-errors builds passed with zero warnings/errors for Core.Tests, Infrastructure.Tests, IntegrationTests, and Replay.Tests. Post-format suites passed: Core.Tests 64/64, Infrastructure.Tests 35/35, IntegrationTests 5/5, Replay.Tests 5/5. Scoped C# format, docs validation, `git diff --check`, and canonical source SHA verification passed.

## P04-M01-T001 evidence

- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Exchange.Binance.Tests\TradingBot.Exchange.Binance.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~BinanceRestClientTests` initially failed with the expected missing `BinancePublicRestClient` compile error before production implementation.
- `BinancePublicRestClient` and immutable REST DTOs cover exchangeInfo, server time, price/24-hour tickers, klines, recent/aggregate trades, mark/funding, funding history/info, and current open interest. String decimals remain exact; raw exchange milliseconds remain available; open klines stay outside Core `Bar`.
- Fixture evidence covers object/array response shapes, current paths/query values, decimal precision, raw times, kline closure, buyer-maker trade side, Core mapping, caller cancellation, HTTP 429, Binance error code/message, and response-size limits.
- Release Exchange.Tests build with warnings as errors passed with zero warnings/errors; all Exchange.Tests passed 7/7; scoped format verification passed; documentation validation passed with first READY task `P04-M01-T002`; `git diff --check` passed; canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P04-M01-T002 evidence

- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Exchange.Binance.Tests\TradingBot.Exchange.Binance.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~BinanceWebSocketClientTests` initially failed with the expected missing WebSocket contract/client types before production implementation.
- `BinanceMarketWebSocketClient` keeps `/market` as the required route, sends live `SUBSCRIBE` commands after every connection, optionally sets combined mode, assembles fragmented text/binary messages under configured frame/message limits, and parses the five public stream kinds. `ClientWebSocketTransport` wraps BCL `ClientWebSocket` with keepalive.
- Immutable stream envelopes preserve raw JSON, event/exchange times, receive/availability times, a session identifier, and stable per-session sequence. Kline updates use the candle open time as identity, so open updates can evolve without identity conflicts; only closed updates map to Core `Bar`. Liquidations are dedicated events and never become Core `Trade` values.
- Focused fixtures cover duplicate identity, different-value identity conflict, late event time, stale event time, aggregate-trade gaps, disconnect/reconnect/backoff/resubscribe, heartbeat, 24-hour session policy, raw/combined/fragmented frames, cancellation, causal fields, and liquidation separation. Release Exchange.Tests build with warnings as errors passed with zero warnings/errors; all Exchange.Tests passed 15/15; scoped format verification passed.
- Documentation validation passed with first READY task `P04-M01-T003`; `git diff --check` passed; canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P04-M01-T003 evidence

- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Exchange.Binance.Tests\TradingBot.Exchange.Binance.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~BinanceDepthAndOrderBookTests` initially failed with the expected missing depth snapshot, stream, recovery, and transport-factory contracts before production implementation.
- The depth REST adapter covers `/fapi/v1/depth`, official limits, exact-decimal levels, raw `lastUpdateId`/`E`/`T`, and local HTTP receive/availability provenance. `BinanceDepthWebSocketClient` is pinned to `/public/stream`, parses raw/combined fragmented depth updates, preserves causal envelope fields, bounds frames/messages/deduplication/reconnects, emits diagnostics, and creates a fresh transport per session through the test seam.
- `BinanceLocalOrderBookRecovery` uses bounded snapshot/diff buffering, overlap alignment, `pu`/range contiguity checks, bounded snapshot levels and retries, fail-closed recovering states, immutable sorted ready states, and snapshot/update provenance. The existing `/market` client rejects depth subscriptions.
- Release Exchange.Tests build with warnings as errors passed with zero warnings/errors; all Exchange.Tests passed 21/21; scoped format verification passed. Documentation validation passed with `P04-M01-T003` complete and `P04-M02-T001,P04-M03-T001` listed READY; `git diff --check` passed; canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P04-M03-T001 evidence

- TDD RED: the Core capture and Infrastructure persistence test projects initially failed with the expected missing `CaptureEnvelope`/`CaptureProvenance` and `ParquetCaptureStore`/schema-6 contracts before implementation.
- Core adds immutable CaptureEnvelope v1, provenance, diagnostics, causal timestamps, stable/capture sequences, and depth/trade/diagnostic-only validation. Infrastructure adds Parquet.Net 6.1.0 persistence with explicit row, buffer, and byte bounds; fsynced temporary files, atomic rename, serializable segment/checkpoint commit, cleanup on failed publish, configurable flush-on-dispose, and restart validation. SQLite migration 5 -> 6 creates `capture_segments` and `capture_checkpoints` while preserving prior rows.
- Capture tests cover canonical JSON, immutable causal/provenance fields, bounded multi-segment writes, oversize-file rejection without published metadata/file, sequence and buffer bounds, restart recovery, and 5 -> 6 migration preservation. Integration coverage closes and reopens the store with a public trade capture and verifies its checkpoint/provenance. No strategy, order, or execution state is included.
- Unlocked and locked restore passed. Release Core.Tests, Infrastructure.Tests, IntegrationTests, and Exchange.Tests builds with warnings as errors passed with zero warnings/errors. Release suites passed Core 67/67, Infrastructure 39/39, Integration 6/6, and Exchange 21/21. Scoped C# format verification passed; documentation validation passed with `P04-M02-T001,P04-M03-T002` listed READY; `git diff --check` passed; canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P04-M03-T002 evidence

- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Infrastructure.Tests\TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CaptureDatasetManifestTests"` initially failed with the expected missing manifest, bounded reader, and retention types before implementation.
- `CaptureDatasetManifest` is an immutable format-one allowlist containing the fixed stream/checkpoint, segment bounds and sizes, source/instrument/session provenance, observed causal-time bounds, ordering/configured limits, explicit unknown coverage, and separate gap/exclusion evidence. `CaptureDatasetManifestBuilder` aggregates durable Parquet segments through an explicit incremental segment/record bound; it omits raw payloads and absolute segment paths and rejects source userinfo, sensitive query keys, absolute paths, and listenKey-like values.
- `CaptureRetentionPlanner` preserves nullable unknown limits, accepts only caller-supplied UTC `asOf`, uses durable segment time and deterministic byte tie-breaks, reports candidate reasons, and returns `Unknown` with no candidates when both limits are unknown. Dry-runs expose no deletion or checkpoint mutation API. Atomic manifest persistence and reopen are pinned to the durable checkpoint.
- Focused `CaptureDatasetManifestTests` passed 7/7. Release Infrastructure.Tests build with warnings as errors passed with zero warnings and errors; scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. No data deletion, credential, private API, or Core/schema change was performed.

## P04-M03-T003 evidence

- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Infrastructure.Tests\TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CaptureQualitySnapshotTests"` initially failed with the expected missing `CaptureQualitySnapshotBuildOptions` type before implementation.
- `CaptureQualitySnapshotBuilder` rebuilds deterministic quality evidence for the same manifest/checkpoint using bounded durable reads. It counts documented `gap`, `duplicate`, `missing-field`, `clock-skew`, and `stale` signals once per record even when represented by both a quality flag and diagnostic code; unknown diagnostic codes remain visible. Latency and last receive are observed values only, and sequence gaps or latency do not infer clock skew.
- Caller-supplied UTC `asOf` and nullable freshness/provider-silence/clock-skew thresholds produce explicit statuses; missing thresholds stay `Unknown`, empty/no-new records produce provider silence when a threshold is known, and no numeric project TTL/default is introduced. Atomic snapshot persistence links manifest ID/checkpoint, and fixed BCL meters publish counters once per snapshot fingerprint plus low-cardinality observable gauges.
- Focused `CaptureQualitySnapshotTests` passed 4/4, including equal/late times, duplicate evidence, unknown code/thresholds, empty/no-new silence, restart rebuild, and `MeterListener` measurements. The focused capture integration test passed 1/1. Release Infrastructure.Tests and IntegrationTests builds with warnings as errors passed with zero warnings and errors; scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. No Core contract, SQLite schema, deletion, credential, private API, or order behavior was added.

## P04-M02-T001 evidence

- `docs/architecture/private-protocol.md` catalogs all current USDⓈ-M private REST methods in the official Account and Trade sections, including Algo methods, plus the three listenKey methods and the current user-data event inventory.
- Each endpoint row records method, full production URL, confirmed parameters and response fields or `UNKNOWN`, security/signature, published IP/order limits or `UNKNOWN`, documented errors/time constraints, official source URL, and checked date 2026-10-15. The New Algo Order narrative versus `type` enum conflict is explicitly `NEEDS_VALIDATION`; no client behavior is inferred from it.
- Official source review, `pwsh -NoProfile -File tools/validate-docs.ps1`, `git diff --check`, and canonical source SHA-256 verification passed. No Binance API endpoint, private credential, or order was used.

## P04-M02-T002 evidence

- Test-only synthetic fixtures and helpers in `tests/TradingBot.Exchange.Binance.Tests` preserve the confirmed `ALGO_UPDATE` envelope fields `e`, `E`, `T`, and opaque object `o` from the official schema checked 2026-10-15. Nested `o` content remains opaque where the official schema does not confirm it.
- Fixtures keep the algo identifier separate from the session/stable-sequence event identity, retain exchange `E`/`T` and local receive/availability timestamps, and cover repeated payloads, same-algo state transitions, equal and late exchange timestamps, unknown fields/status values, and malformed confirmed fields. No global identifier uniqueness or transport/auth behavior is assumed.
- Focused `BinanceAlgoProtocolFixtureTests` passed 5/5. Exchange.Tests Release build with `--warnaserror` passed with 0 warnings and 0 errors; scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. No Binance API endpoint, credential, or order was used.

## P04-M02-T003 evidence

- `docs/architecture/private-protocol.md` now records the official General Info and user-data freshness/error rules checked 2026-10-15, including explicit synthetic test intervals, project TTL `UNKNOWN`, listenKey expiry/disconnect handling, clock skew, rate/auth diagnostics, HTTP 503 variants, and reconciliation for unknown outcomes. It records that an absent order response does not prove non-execution and that ordinary-order guarantees are not transferred to Algo without an official source.
- `BinancePrivateProtocolEvidenceEvaluator` is test-only and returns diagnostic reasons only. `BinancePrivateProtocolEvidenceTests` covers incomplete/stale evidence, unknown statuses, disconnect/listenKey expiry, clock skew, rate limits, authentication failures, ambiguous outcomes, missing order responses, and 503 unknown/service-unavailable/internal/throttled variants. Combined Algo/evidence fixture tests passed 15/15.
- Exchange.Tests Release build with `--warnaserror` passed with 0 warnings and 0 errors; scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. No Binance API endpoint, credential, or order was used.

## Scope limits

- P03-M01-T002 adds only the versioned replay envelope, SQLite schema 1 -> 2 migration, deterministic bounded Backtesting snapshot, focused tests, architecture notes, and this handoff.
- P03-M02-T001 adds only durable order-intent state; P03-M02-T002 adds only passive portfolio limits/reservations, shared schema 3 -> 4 migration, and atomic intent-plus-reservation persistence; P03-M02-T003 adds only passive operational fills, position snapshots, reconciliation checkpoints, and guarded full-reservation release. Binance calls, credentials, venue adapters, execution, StrategyRun, ConfigVersion, capture manifests, retention policy, and recovery policy remain future tasks.
- P04-M01-T001 adds only public REST market-data transport/normalization and fixtures. It does not add private endpoints, credentials, orderbook/depth recovery, WebSocket transport, strategy behavior, retries, or orders.
- P04-M01-T002 adds only public WebSocket parsing, bounded reconnect/resubscribe, causal envelopes, diagnostics, and fixtures. P04-M01-T003 adds only public depth REST/`/public/stream` transport, bounded local snapshot/diff recovery, provenance, and fixtures; it adds no private API, strategy, orders, or capture persistence.
- P04-M03-T001 adds only passive Core capture envelopes and bounded depth/trade/diagnostic Parquet persistence with schema-6 metadata/checkpoints. It adds no strategy, orders, execution, private API, or capture manifest/retention policy.
- P04-M03-T002 adds only Infrastructure manifest/retention metadata and test fixtures. It adds no Core contract, SQLite schema migration, capture deletion, exporter, API call, credential, strategy, order, or execution behavior.
- P04-M02-T001 adds only the official-source private protocol catalogue and user-data event inventory; it adds no private transport, credentials, signing implementation, order, or execution behavior.
- P04-M02-T002 adds only synthetic Algo Service/`ALGO_UPDATE` fixtures and test-only parsing/identity helpers; it adds no production transport, authorization, private API call, order, or Core contract.
- P04-M02-T003 adds only a test-only freshness/error diagnostic evaluator and evidence matrix documentation; it adds no execution permission, retry logic, private transport, credential, order, or Core contract.
- P05-M01-T001 adds only immutable exchange eligibility metadata, exact-decimal Binance filter extensions, deterministic unknown-field retention, local receive/availability provenance, Core mapping, official-source notes, and focused fixtures. P05-M01-T002 adds only the metadata-only candidate filter and deterministic exclusion fixtures. These tasks add no screener/features, ranking, pool limits, persistence, order, or execution behavior.
- P05-M02-T001 adds only the immutable causal screener input/configuration contracts, shared cursor selection, and six pure minimum feature formulas with explicit warmup/unknown reasons. P05-M02-T002 adds only caller-supplied TTL freshness/completeness metadata and deterministic causal quality handling for duplicates, conflicts, gaps, and late observations. These tasks add no ranking, pool limits, snapshots, order, or execution behavior.
- Remote CI success remains unverified.

## P05-M01-T001 evidence

- TDD RED: the new Core metadata and Binance exchangeInfo fixtures initially failed with the expected missing `ExchangeEligibilityMetadata`, filter extension, and provenance members before production implementation.
- Core metadata retains exact decimal `PRICE_FILTER`, `LOT_SIZE`, and `MIN_NOTIONAL` values, immutable sorted unknown fields, official symbol extensions, onboarding/delivery timestamps, and separate local receive/availability timestamps. The Binance adapter captures local provenance around `/fapi/v1/exchangeInfo`; it retains raw `serverTime` without using it as a clock source and exposes a Core metadata mapping path.
- Official source facts checked 2026-10-16 are recorded in [`docs/architecture/market-data.md`](docs/architecture/market-data.md): exchangeInfo weight 1, public/no-key boundary, documented filter fields and zero PRICE_FILTER semantics, precision fields not being tick/step substitutes, and freshness/TTL remaining `UNKNOWN`.
- `C:\temp\dotnet10\dotnet.exe restore tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj --locked-mode` and the equivalent Exchange.Tests restore both exited 0. Release warnings-as-errors builds for both test projects exited 0 with 0 warnings and 0 errors. Focused `ExchangeEligibilityMetadataTests` passed 2/2; focused exchangeInfo parser/provenance tests passed 2/2. Scoped `dotnet format TradingBot.slnx --no-restore --verify-no-changes --include` over all changed C# files exited 0.
- The T001 gate passed before T002 began: `pwsh -NoProfile -File tools/validate-docs.ps1` confirmed `P05-M01-T002` READY; `git diff --check` and canonical source SHA-256 `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54` passed.

## P05-M01-T002 evidence

- TDD RED: the new metadata filter fixtures initially failed with the expected missing `MetadataCandidateFilterResult` and `MetadataCandidateFilter` types before production implementation.
- `MetadataCandidateFilter.Evaluate` includes only `PERPETUAL` + `TRADING` symbols with `USDT` quote and margin assets. It requires and validates `PRICE_FILTER`, `LOT_SIZE`, and `MIN_NOTIONAL`, validates present recognized optional filters, retains additional filters as unknown metadata, and excludes malformed or duplicate/conflicting metadata with stable ordinal reasons. Zero `PRICE_FILTER` values follow the official disabled-rule semantics.
- The deterministic Core fixtures cover repeatability, shuffled inputs, ordinal symbol ordering, cultures, USDT/USDC and contract/status gates, missing/conflicting filters, zero `PRICE_FILTER` values, recognized optional fields, and additional unknown filters. Focused Core metadata/filter tests passed 7/7; focused Binance exchangeInfo parser/provenance/receive-time tests passed 3/3. A regression test first reproduced the pre-request receive-time bug, then passed after receive time was captured when the HTTP document became available.
- `C:\temp\dotnet10\dotnet.exe restore tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj --locked-mode` and the equivalent Exchange.Tests restore both exited 0. Release warnings-as-errors builds for both test projects exited 0 with 0 warnings and 0 errors. Scoped `dotnet format TradingBot.slnx --no-restore --verify-no-changes --include` exited 0. `pwsh -NoProfile -File tools/validate-docs.ps1` passed with `ready=P05-M02-T001`; `git diff --check` exited 0; and the canonical source SHA-256 remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P05-M02-T001 evidence

- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ScreenerFeatureCalculatorTests` initially failed with the expected missing screener contract types before production implementation.
- `ScreenerFeatureInput` preserves source/instrument identity, event/receive/availability timestamps, stable sequence, immutable closed bars with exact quote volume, ready bid/ask observations, and OI observations. `ScreenerFeatureConfiguration` requires explicit timeframe, lookbacks, and formula version; omitted or invalid values remain `UnknownConfiguration` with no numeric defaults. `ScreenerCausalSelection` filters by the inclusive event-time/availability-time/stable-sequence cursor before deterministic ordering.
- `ScreenerFeatureCalculator.Calculate` is pure and returns exactly RVOL, price acceleration, ATR expansion, quote-volume turnover, spread, and OI anomaly. The implementation uses decimal without trading rounding, applies the specified `N+1`, `2L+1`, `2N+1`, `K`, one-quote, and `M+1` warmups, and reports warmup, input, zero-denominator, and overflow reasons. No ranking, threshold, freshness, persistence, or execution behavior was added.
- Focused screener fixtures passed 8/8; full `TradingBot.Core.Tests` passed 82/82. Locked Core.Tests restore exited 0; Release Core.Tests build with `--warnaserror` exited 0 with zero warnings/errors; scoped format verification exited 0. Architecture details are recorded in [`docs/architecture/screener-features.md`](docs/architecture/screener-features.md).
- Documentation validation, `git diff --check`, and canonical source SHA-256 verification were run after the task transition; remote CI remains unverified.

## P05-M02-T002 evidence

- `ScreenerFeatureValue` now retains nullable value, completeness, freshness, age, latest required availability, and immutable stable reasons. `ScreenerFreshnessPolicy` accepts only caller-supplied positive TTLs by default, feature family, or source; missing TTL remains `Unknown`, age exactly equal to TTL is `Fresh`, and age greater than TTL is `Stale`. The calculator never reads a clock, and effective `IsReady` requires a valid complete formula and fresh inputs.
- Causal calculation filters every observation by inclusive event, availability, and stable-sequence bounds, and filters bars by inclusive `Bar.CloseTime <= cursor.AsOf`. Canonicalization is ordinal and permutation independent: exact duplicates count once, same-key conflicts and bar gaps produce stable quality reasons, late availability remains visible, and missing values are never filled or interpolated. Six T001 formulas and their golden behavior remain intact.
- TDD RED: the focused screener test command initially failed with the expected missing `ScreenerFreshnessPolicy` contract before T002 implementation. Focused `ScreenerFeatureCalculatorTests` passed 13/13 after implementation.
- `C:\Users\Администратор\.dotnet\dotnet.exe restore TradingBot.slnx --locked-mode` exited 0. `C:\Users\Администратор\.dotnet\dotnet.exe build TradingBot.slnx -c Release --warnaserror --no-restore` exited 0 with zero warnings and errors. Scoped format verification over `ScreenerFeatureContracts.cs` and `ScreenerFeatureCalculatorTests.cs` exited 0.
- `& .\tools\validate-docs.ps1` exited 0 with phases=19, milestones=41, tasks=119, ready=P05-M02-T003. `git diff --check` exited 0. Canonical `docs/sources/FINAL_TECH_SPEC.md` SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P05-M02-T003 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ScreenerSnapshotContractTests"` initially failed with the expected missing `ScreenerSnapshot` type before the Core implementation.
- `ScreenerSnapshot` normalizes causal input/value ordering and feature reasons through one canonical Core JSON path. It carries format version 1 separately from supported formula version `screener-v1`, preserves explicit nullable lookbacks and TTLs, and computes a SHA-256 identity without identity or current time in the payload. Reordered equivalent inputs receive the same identity; persisted input re-evaluates to the stored feature values.
- `SqliteDatabase` now migrates schema 6 -> 7 transactionally and validates the BLOB-keyed `screener_snapshots` table. `SqliteScreenerSnapshotStore.Append` is idempotent for exact retries, rejects corrupt/conflicting content and unsupported versions on read, commits before returning success, and rolls back under a held writer. Existing event, intent, reservation, operational, and capture rows remain readable after migration.
- Final focused verification passed screener fixtures 16/16 (including snapshot contracts 3/3), snapshot persistence fixtures 6/6, and affected existing Infrastructure persistence fixtures 38/38. Release warnings-as-errors builds for Core.Tests and Infrastructure.Tests passed with zero warnings/errors; locked restore, scoped format, docs validation, `git diff --check`, and canonical source SHA-256 passed. Full-suite and remote CI remain unverified.

## P05-M03-T001 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~StagedSelectionTests"` initially failed with the expected missing `StagedSelectionKey` and `StagedSelectionConfiguration` contracts before production implementation.
- Core adds immutable format-one staged-selection configuration, input, result, candidate, exclusion, and comparison contracts. `UniverseSelectionStage` gates metadata by local availability at one caller-supplied cursor, applies `MetadataCandidateFilter`, collapses exact duplicate screener snapshot identities, rejects conflicting snapshots and future causal observations, recalculates from snapshot inputs, requires configured features to be complete and fresh, ranks by explicit Turnover/Spread lexical directions with ordinal symbol tie-break, and preserves metadata, snapshot evidence, recalculated values, comparisons, ranks, reasons, filter evidence, and cursor/configuration provenance. Capacity is bounded at 250 with no weights, defaults, TTLs, setup, order, or execution behavior.
- Focused staged fixtures passed 6/6, including availability/readiness, deterministic ranking and capacity, duplicate/conflict/no-lookahead behavior, culture/permutation stability, and canonical Core JSON round trip. Locked Core.Tests restore, Release warnings-as-errors build, scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. `P05-M03-T002` was the next validator-confirmed READY task; P05-M01-T003 was blocked by P05-M03-T003 at that transition.

## P05-M03-T002 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CandidatePoolSelectionTests"` initially failed with the expected missing `CandidatePoolSelectionStage`, `PreviousStage`, and `StageIdentity` contracts before production implementation.
- Core adds `CandidatePoolSelectionStage`, an explicit format-one `StagedSelectionStageReference`, and replay-stable SHA-256 result identity. Candidate reduction accepts only the selected Universe membership and the Universe cursor, rejects new instruments, cursor/future/late/conflicting screener evidence, recalculates from saved inputs, gates only configured six-feature keys, and preserves previous metadata, evidence, comparisons, ranks, reasons, capacity exclusions, and prior-stage identity/membership. Missing optional feature inputs remain non-blocking when their keys are not configured.
- Six focused CandidatePool fixtures passed 6/6, covering prior-membership restriction and provenance, optional-feature readiness, conflict/future rejection, all six explicit keys, permutation/culture-independent results, capacity 50, and canonical JSON round trip. T001 staged fixtures passed 6/6; focused metadata/filter, screener, snapshot/serialization, architecture, and Core boundary fixtures passed; locked Core.Tests restore and Release Core.Tests build with warnings as errors passed with zero warnings/errors. Scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. `P05-M03-T003` was the next validator-confirmed READY task; P05-M01-T003 was blocked by P05-M03-T003 at that transition.

## P05-M03-T003 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RealtimeMonitorSelectionTests"` initially failed with the expected missing `StagedSelectionChainSnapshot` contract before production implementation.
- Core adds `RealtimeMonitorSelectionStage`, which accepts only a CandidatePool result and same-cursor screener evidence. It enforces the CandidatePool membership, explicit six-feature configuration, complete/fresh readiness only for configured keys, deterministic lexical ranking, conflict/future handling, provenance, and capacity 20. The CandidatePool reference must point to Universe before monitor evaluation.
- `StagedSelectionChainSnapshot` stores the shared cursor, Universe input, per-stage screener evidence, all three configurations/results, stage exclusions/provenance, and format-one algorithm version. It normalizes canonical evidence order, validates Universe→CandidatePool→RealtimeMonitor identity/cursor/membership links, rebuilds all three stages on replay, and computes a lower-case SHA-256 identity excluded from canonical JSON. Altered links, skipped stages, unsupported format, and unsupported algorithm versions are rejected.
- Six RealtimeMonitor/chain fixtures passed 6/6, covering membership/provenance, configured-key readiness, conflict/future evidence, capacity 20, canonical round trip, same identity after replay, and altered/unsupported chain rejection. T001 and T002 staged fixtures passed 6/6 each; focused metadata/filter, screener, snapshot/serialization, architecture, and Core boundary fixtures passed; locked Core.Tests restore and Release Core.Tests build with warnings as errors passed with zero warnings/errors. Scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. `P05-M01-T003` is the next validator-confirmed READY task; it was not implemented.

## P05-M01-T003 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Infrastructure.Tests\TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SqliteUniverseSelectionSnapshotStoreTests"` initially failed with the expected missing `SqliteUniverseSelectionSnapshotStore` type; fixture-only timestamp/identity issues were corrected before production implementation.
- `SqliteUniverseSelectionSnapshotStore` persists canonical `StagedSelectionChainSnapshot` JSON in the BLOB-keyed `universe_selection_snapshots` table. It exposes `Open`, `Append`, and `Find` by canonical hex or 32-byte SHA-256, returns false for exact retries, validates row format/algorithm metadata, content, canonical JSON, identity, and pure replay, and fails closed for corruption, conflicts, unsupported versions, altered chain content, non-canonical payloads, identity mismatches, and write-lock failures.
- `SqliteDatabase` now migrates schema 7 -> 8 transactionally and validates the exact four-column WITHOUT ROWID table. New databases and older schema fixtures reach version 8 while preserving prior event, screener, order, portfolio, operational, and capture rows. Existing schema-6 and schema-5 fixtures drop the test-only newer table before modeling the older version.
- The focused store fixtures passed 7/7. Related snapshot/migration/persistence fixtures passed 51/51: `SqliteScreenerSnapshotStoreTests` 6/6, `SqliteEventJournalTests` 9/9, `ReplayEnvelopeJournalTests` 2/2, `SqliteOrderIntentJournalTests` 6/6, `SqlitePortfolioReservationStoreTests` 11/11, `SqliteOperationalStateTests` 6/6, and `ParquetCaptureStoreTests` 4/4. Release Infrastructure.Tests build with warnings as errors passed with zero warnings/errors; locked restore, scoped format, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. No rolling indicators, setups, execution, or production mutation were added.
- Validator confirmed `ready=P06-M01-T001`; the rolling-indicator task is only READY and was not started.

## P06-M01-T001 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RollingIndicatorTests"` initially failed with the expected missing rolling-indicator contract types before production implementation.
- `RollingIndicatorCalculator` is a pure BCL-only Core calculator for ATR, SMA/EMA/WMA/RMA, and RVOL. The explicit `rolling-v1` configuration has no defaults, weights, thresholds, or TTLs. It filters event time, availability time, stable sequence, and closed-bar time at one `CausalEventCursor`, respects `HistoryStart`, calculates recursive seeds from the full available history, and retains canonical input, seed, quality, warmup, and exclusion provenance.
- Checked decimal arithmetic reports overflow and zero RVOL denominators explicitly. Exact duplicates collapse deterministically; conflicts and timeframe gaps block readiness without filling. Future or unavailable observations cannot alter an earlier cursor result. Strict Core JSON round trip, culture/permutation determinism, empty-input warmup, invalid configuration/input states, and Core immutability/boundary fixtures are covered.
- Focused rolling fixtures passed 9/9. Affected screener/serialization/boundary fixtures passed: `ScreenerFeatureCalculatorTests` 13/13, `ScreenerSnapshotContractTests` 3/3, `CoreJsonSerializationTests` 7/7, `ArchitectureTests` 1/1, and `CoreBoundaryTests` 2/2. Locked Core.Tests restore and Release Core.Tests build with warnings as errors passed with zero warnings/errors; scoped format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. P06-M01-T002 is the next validator-confirmed READY task and was not implemented.

## P06-M01-T002 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PriceContextFeatureTests"` initially failed at compile time with the expected missing `PriceVolumeObservation` Core contract before production implementation.
- `PriceContextFeatureCalculator` adds immutable `price-context-v1` Core contracts and pure calculations for one-bar floor pivots, rolling dynamic support/resistance, a configured Donchian-style channel, exact caller-supplied price-volume VWAP, explicitly binned POC, and configured SMA/EMA/WMA/RMA trend state. All lookbacks, POC bin size, moving-average kind, and version are caller supplied; missing or unsupported values remain feature-local unknown states.
- Causal selection applies inclusive event-time, availability-time, stable-sequence, and closed-bar bounds at one cursor. Canonical duplicate handling, conflicting/gapped input quality, late/future reasons, checked decimal overflow, zero-volume denominators, culture/permutation stability, and strict `CoreJsonSerializer` round trip are covered. OHLCV is never substituted for exact VWAP/POC source data.
- Focused PriceContextFeatureTests passed 7/7. Release Core.Tests build with warnings as errors passed with zero warnings/errors. Scoped format verification over `PriceContextFeatureContracts.cs` and `PriceContextFeatureTests.cs` passed. After the T002 row transition, `pwsh -NoProfile -File tools/validate-docs.ps1` passed with `ready=P06-M01-T003`.

## P06-M01-T003 evidence

- `PriceContextReplayRegressionTests` adds eleven independent, hand-specified vectors for cursor-by-cursor batch/prefix replay parity, future-data no-lookahead, strict pivot confirmation timing, EMA/RMA close and HLC3 EMA/RMA centers, ATR and population-standard-deviation widths, frozen ATR break equality/wick/reversal, HTF distinct-bar confirmation and candidate/unknown reset including a pending Bear duplicate, late availability, duplicate/conflict handling, invalid/oversized/warmup/decimal-overflow configuration, exact population-SD sqrt rounding, permutation/culture determinism, and strict JSON round trip.
- Focused replay vectors passed 11/11; all PriceContext tests passed 27/27; RollingIndicator tests passed 9/9; Core JSON/boundary tests passed 9/9. Release Core.Tests build with `--warnaserror` passed with zero warnings/errors, scoped C# format verification passed, and the canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- `pwsh -NoProfile -File tools/validate-docs.ps1` passed with `phases=19, milestones=41, tasks=119, ready=P06-M02-T001` after the T003 row transition. `git diff --check` passed. No calculator or contract changes were needed for T003; no persistence, strategy, ranking, API, order, execution, or later Phase 6/7 behavior was added.

## P06 correction review and restart

- The earlier P06-M01-T002 evidence above records the original `price-context-v1` implementation and is superseded: review against canonical source §§7–10 and 13 found floor pivots, rolling extrema, a Donchian channel, and MA-cross trend instead of the specified confirmed structural pivots, ATR channel, dynamic trend rules, and separate HTF context. Invalid numeric periods and enum values also escaped as exceptions rather than feature-local unknown results.
- P06-M01-T002 was reopened as `IN_PROGRESS`, and the original v1 completion proof is superseded. Final review also found that an invalid unused ATR smoothing enum blocked a StandardDeviation channel; a red/green regression fixed the feature-local configuration dependency. Corrected T002 and the independent T003 replay regression are now `DONE`; P06-M02-T001 is now `READY`.
- Corrective work uses explicit configuration, strict full-window pivots with causal confirmation, nullable ATR evidence when a confirmation-time buffer cannot be calculated, frozen-buffer role reversal, caller-selected EMA/RMA smoothing for HLC3, ATR or population-standard-deviation channel width, local trend, and HTF confirmed context. Shared rolling math kernels preserve `rolling-v1` operation order. The source specification remains immutable.
- Final T002 verification: focused PriceContext, RollingIndicator, JSON serialization, Core boundary, and architecture tests passed 35/35; Core.Tests Release build with `--warnaserror` had 0 warnings/errors; scoped format, `git diff --check`, canonical source SHA-256, and `tools/validate-docs.ps1` passed. The validator reports `ready=P06-M01-T003`.
- T003 evidence above records the independent cursor-by-cursor replay, no-lookahead, strict pivot, late-data, duplicate/conflict, culture, serialization, pending-duplicate, reset, and deterministic population-SD vectors and their focused verification results. The validator confirms `P06-M02-T001` as the next READY task.

## P06-M02-T002 evidence

- TDD RED: focused Core snapshot compilation initially failed with the missing `FeatureQualitySnapshot` contract; focused Infrastructure store compilation initially failed with the missing `SqliteFeatureQualitySnapshotStore`, and the MeterListener fixture initially failed with the missing `FeatureQualityMetricsPublisher`.
- `FeatureQualitySnapshot` is format 1 with `feature-quality-v1`, retains complete rolling/price-context results, explicit policy, cursor, source formula versions, and quality, and computes a lower-case SHA-256 identity from canonical JSON excluding identity. `SqliteFeatureQualitySnapshotStore` provides Open, idempotent Append, and hex/32-byte Find; it validates schema metadata, versions, canonical JSON, identity, and evaluator recomputation.
- `SqliteDatabase` now migrates schema 8 -> 9 transactionally and validates the exact BLOB-keyed WITHOUT ROWID `feature_quality_snapshots` table. Existing older fixtures that retain the table are handled idempotently, while malformed definitions still fail exact validation. `FeatureQualityMetricsPublisher` emits readiness, stable reasons, market-data age, availability age, and known coverage with explicit unknown measurements.
- Focused Core quality/snapshot tests passed 29/29; focused feature-quality store/metrics tests passed 2/2; the relevant Infrastructure suite passed 65/65; locked restore and Release Infrastructure build with `--warnaserror` passed with zero warnings/errors. Persistence and feature-quality architecture notes were updated without changing the canonical source or future-phase behavior.
- `pwsh -NoProfile -File tools/validate-docs.ps1` is required immediately after this transition; the expected next validator-selected task is `P06-M02-T003`. No setup, execution, Phase 7, or Lane C behavior was added.

## P06-M02-T003 evidence

- TDD RED: `FeatureQualityRegressionTests.InvalidTtlIsPersistableAndRemainsUnknown` first failed because the evidence contract rejected a non-positive TTL instead of preserving the explicit invalid policy as Unknown. The minimal Core fix removed that rejection; the focused regression suite then passed 6/6.
- The independent synthetic Core fixture set covers TTL epsilon/equality/+epsilon, delayed old availability, absent and invalid policies, warmup, gaps, duplicates, conflicts, isolated rolling/setup/HTF/price-volume dependencies, causal future boundaries/no-lookahead, permutation/culture determinism, and strict JSON. Related FeatureQuality/Rolling/PriceContext checks passed 46/46.
- Infrastructure fixtures cover reopen/replay migration preservation, idempotent append, hex and 32-byte lookup, non-canonical and corrupt rows, unsupported format/algorithm metadata, recomputation mismatch, exact schema validation, held-writer rollback, and MeterListener readiness/reasons/both ages/explicit unknown coverage. Focused feature-quality store fixtures passed 4/4 and full Infrastructure tests passed 67/67.
- Release Core.Tests and Infrastructure.Tests builds with `--warnaserror` passed with zero warnings/errors; locked restore passed; scoped format verification passed. After the DONE transition, `pwsh -NoProfile -File tools/validate-docs.ps1` must pass with `P07-M01-T001` as first READY and `P13-M01-T001` also READY. The milestone boundary is complete; no Phase 7 or Lane C implementation was started.

## P07-M01-T001 evidence

- Startup protocol: worktree was clean at `5940f2a`; the bundled PowerShell 7.6.5 validator reported `phases=19, milestones=41, tasks=119, ready=P07-M01-T001,P13-M01-T001`. `P07-M01-T001` was the first READY task in document order and `P06-M02-T003` was DONE.
- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~BoundaryRejectionSetupTests"` failed before production code with the expected `CS0246` errors for missing `BoundaryRejectionSetupResult` and the other Boundary Rejection contracts.
- Core adds immutable `BoundaryRejectionSetupVersion`, explicit nullable `BoundaryRejectionSetupConfiguration`, `BoundaryRejectionConditionEvidence`, `BoundaryRejectionSetupObservation`, and `BoundaryRejectionSetupResult` contracts. `BoundaryRejectionSetupEvaluator.Evaluate` is pure and BCL-only; it consumes `price-context-v2` and `feature-quality-v1` at the same `CausalEventCursor`, checks the confirmed signal bar against the channel/local-trend/HTF evidence, and returns both directions.
- LONG/SHORT implement SETUP-01 exactly: confirmed HTF Bull/Bear, LocalTrend Bull/Bear, `Low <= Lower` plus strict `Close > Lower` (mirrored for short), causally eligible confirmed signal bar, caller-supplied positive minimum channel width against the full `Upper - Lower` span, and exactly one ready Channel/LocalTrend/HtfContext quality record matching formula, original state, and series. Unused VWAP/POC/pivot/level quality does not gate this family. Missing/invalid configuration, warmup, stale/incomplete/mismatched quality, and ineligible or inconsistent signal bars fail closed as explicit unknown evidence. Strict close-through is `Invalidated`; equality at a band is `NoSetup`.
- Focused GREEN: `BoundaryRejectionSetupTests` passed 11/11. Full `TradingBot.Core.Tests` passed 165/165. Locked restore of `TradingBot.Core.Tests` exited 0; Release build with `--warnaserror` exited 0 with zero warnings/errors. New C# files were normalized to UTF-8 BOM + CRLF, and scoped `dotnet format --verify-no-changes` over the three new files exited 0.
- Documentation: [`docs/architecture/boundary-rejection.md`](docs/architecture/boundary-rejection.md) records the SETUP-01 mapping, full-span minimum-width interpretation, quality gate, states, invalidation, and causality. [`docs/architecture/feature-quality.md`](docs/architecture/feature-quality.md) records the selective setup consumption. `docs/TASKS.md` marks T001 DONE and T002 READY; `HANDOFF.md` records this checkpoint.
- After the transition, the same bundled PowerShell validator exited 0 with `phases=19, milestones=41, tasks=119, ready=P07-M01-T002,P13-M01-T001`; `git diff --check` exited 0; canonical `docs/sources/FINAL_TECH_SPEC.md` SHA-256 remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Scope: changes are limited to `TradingBot.Core`, `TradingBot.Core.Tests`, and the listed documentation. No lifecycle, persistence, risk, order, execution, package, project-graph, or canonical-source change was made. No commit or push was performed. `P07-M01-T002` is only READY and was not started; `P13-M01-T001` remains READY and was not started; blocked tasks were not touched. Remote CI remains unverified.

## P07-M01-T002 evidence

- Startup protocol: worktree already contained the uncommitted, verified P07-M01-T001 changes at `5940f2a`; the bundled PowerShell 7.6.5 validator reported `phases=19, milestones=41, tasks=119, ready=P07-M01-T002,P13-M01-T001`. `P07-M01-T002` was the first READY task in document order and `P07-M01-T001` was DONE.
- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CenterRetestSetupTests"` failed before production code with the expected `CS0246` errors for missing `CenterRetestSetupResult` and the other Center Retest contracts.
- Core adds immutable `CenterRetestSetupVersion`, explicit nullable `CenterRetestSetupConfiguration`, `CenterRetestConditionEvidence`, `CenterRetestSetupObservation`, and `CenterRetestSetupResult` contracts. `CenterRetestSetupEvaluator.Evaluate` is pure and BCL-only; it consumes `price-context-v2` and `feature-quality-v1` at the same `CausalEventCursor`, checks the confirmed signal bar against the channel/local-trend/HTF evidence, and returns both directions.
- LONG/SHORT implement SETUP-02: confirmed HTF Bull/Bear, LocalTrend Bull/Bear (the canonical SHORT omission of `local bias` is explicitly mirrored as Bear), `Low <= Center` / `High >= Center`, `Open >= Center` / `Open <= Center`, `Close >= Center` / `Close <= Center`, and strict candle direction. The family uses only `PriceContextChannel.Center`; it has no minimum-width gate, width configuration, or channel-band dependency. Exactly one ready Channel/LocalTrend/HtfContext quality record matching formula, original state, and series is required; unused VWAP/POC/pivot/level quality does not gate this family. Missing/invalid configuration, warmup, stale/incomplete/mismatched quality, and ineligible or inconsistent signal bars fail closed as explicit unknown evidence. Strict close-through is `Invalidated`; equality at the center is `NoSetup`.
- Focused GREEN: `CenterRetestSetupTests` passed 12/12. Full `TradingBot.Core.Tests` passed 177/177. Locked restore of `TradingBot.Core.Tests` exited 0; Release build with `--warnaserror` exited 0 with zero warnings/errors. New C# files were normalized to UTF-8 BOM + CRLF, and scoped `dotnet format --verify-no-changes` over the three new files exited 0.
- Documentation: [`docs/architecture/center-retest.md`](docs/architecture/center-retest.md) records the SETUP-02 mapping, symmetric SHORT local-bias interpretation, no-width-gate decision, quality gate, states, invalidation, and causality. [`docs/architecture/feature-quality.md`](docs/architecture/feature-quality.md) records both Lane A setup consumers. `docs/TASKS.md` marks T002 DONE and T003 READY; this checkpoint is recorded in `HANDOFF.md`.
- After the transition, the same bundled PowerShell validator exited 0 with `phases=19, milestones=41, tasks=119, ready=P07-M01-T003,P13-M01-T001`; `git diff --check` exited 0; canonical `docs/sources/FINAL_TECH_SPEC.md` SHA-256 remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Scope: changes are limited to `TradingBot.Core`, `TradingBot.Core.Tests`, and the listed documentation. The T001 contracts, evaluator, tests, and architecture note were not modified. No lifecycle, persistence, risk, order, execution, package, project-graph, or canonical-source change was made. No commit or push was performed. `P07-M01-T003` is only READY and was not started; `P13-M01-T001` remains READY and was not started; blocked tasks were not touched. Remote CI remains unverified.

## P07-M01-T003 evidence

- Startup protocol: worktree already contained the uncommitted, verified T001/T002 changes at `5940f2a`; the bundled PowerShell 7.6.5 validator reported `phases=19, milestones=41, tasks=119, ready=P07-M01-T003,P13-M01-T001`. `P07-M01-T003` was the first READY task in document order and `P07-M01-T002` was DONE.
- T003 is a test-only characterization/regression suite; no production contract was missing, so there is no compile-time RED. The first baseline exposed two fixture issues only: one short OHLC bar had `low > close`, and the `boundary-*-only` bars close through the Center, so Center Retest correctly reports `Invalidated` for them. Fixtures and expected states were corrected; no production code changed.
- Added `LaneASetupFixtures.cs` and `LaneASetupFixtureTests.cs`. The suite covers a 14-case hand-specified matrix (both families, both directions, Ready/NoSetup/Invalidated/UnknownQuality, and a zero-width Center case proving no width gate), evidence invariants, strict reason ordering, retained snapshot evidence, and V1/execution boundary guards.
- Replay evidence: every matrix case serializes/deserializes `PriceContextFeatureResult`, `FeatureQualityEvaluation`, and both setup results, re-evaluates both families, and compares results with direct evaluation. The real-history cursor-prefix replay covers `Time(1)` through `Time(30)` (10 cursors), serializes the snapshot at every cursor, and reproduces identical evidence; the final cursor is Ready Long for both families.
- No-lookahead/determinism: full-history and prefix calculations preserve historical feature values; future bars cannot make an earlier cursor Ready; reversed input order and `fr-FR`/`en-US` cultures do not change evidence. Strict JSON rejects unknown members.
- Focused GREEN: `LaneASetupFixtureTests` passed 6/6. Full `TradingBot.Core.Tests` passed 183/183. Locked restore of `TradingBot.Core.Tests` exited 0; Release build with `--warnaserror` exited 0 with zero warnings/errors. New test files were normalized to UTF-8 BOM + CRLF, and scoped `dotnet format --verify-no-changes` exited 0.
- Documentation: [`docs/architecture/lane-a-setup-fixtures.md`](docs/architecture/lane-a-setup-fixtures.md) records the matrix, serialized/cursor replay, no-lookahead, determinism, and V1 boundary. `docs/TASKS.md` marks T003 DONE and P07-M02-T001 READY; this checkpoint is recorded in `HANDOFF.md`.
- After the transition, the same bundled PowerShell validator reported `phases=19, milestones=41, tasks=119, ready=P07-M02-T001,P13-M01-T001`; `git diff --check` exited 0; canonical `docs/sources/FINAL_TECH_SPEC.md` SHA-256 remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Scope: changes are limited to `TradingBot.Core.Tests` and the listed documentation. No Core production contracts, evaluator, tests, package, project-graph, or canonical-source change was made. No commit or push was performed. `P07-M02-T001` is only READY and was not started; `P13-M01-T001` remains READY and was not started; blocked tasks were not touched. Remote CI remains unverified.

## P07-M02-T001 evidence

- Startup protocol: worktree already contained the uncommitted, verified P07-M01-T001/T002/T003 changes at `5940f2a`; the bundled PowerShell 7.6.5 validator reported `phases=19, milestones=41, tasks=119, ready=P07-M02-T001,P13-M01-T001`. `P07-M02-T001` was the first READY task in document order and `P07-M01-T003` was DONE.
- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SetupEvidenceSnapshotTests"` failed before production code with the expected `CS0246` errors for missing `SetupEvidenceState`, `SetupEvidenceInputSequence`, `SetupEvidenceInvalidation`, and `SetupEvidenceSnapshot`.
- Core adds immutable `SetupEvidenceState`, `SetupEvidenceInputSequence`, `SetupEvidenceInvalidation`, and `SetupEvidenceSnapshot` contracts. The snapshot is per-direction (`setup-evidence-v1`, format 1), retains the complete `price-context-v2`/`feature-quality-v1` input, exactly one Boundary/Center configuration/observation, a validated causal sequence (cursor, source, instrument, setup/HTF timeframes and history starts, signal bar, family/price-context/quality versions), and explicit invalidation state/ClosedThrough/reasons.
- Constructor validation enforces exactly one family payload, shared cursors, causal-sequence/input agreement, configuration/observation consistency, invalidation consistency with the selected observation, and a fresh family evaluation that must reproduce the observation. `SnapshotId` is the lower-case SHA-256 of canonical JSON excluding identity; `Replay()` re-evaluates the family and reproduces the identity. Tampered direction/setup type/invalidation/cursor/condition payloads are rejected during JSON deserialization.
- Focused GREEN: `SetupEvidenceSnapshotTests` passed 7/7, covering both families and directions, Ready/NoSetup/Invalidated/UnknownQuality, unknown evidence without a signal bar, canonical/direction-specific identity, JSON round-trip and tamper rejection, replay, and no lifecycle/decision/risk/order contracts. Full `TradingBot.Core.Tests` passed 190/190. Locked restore of `TradingBot.Core.Tests` exited 0; Release build with `--warnaserror` exited 0 with zero warnings/errors. New C# files were normalized to UTF-8 BOM + CRLF, and scoped `dotnet format --verify-no-changes` exited 0.
- Documentation: [`docs/architecture/setup-evidence.md`](docs/architecture/setup-evidence.md) records the schema, causal sequence, invalidation, identity/replay, and boundaries. `docs/TASKS.md` marks T001 DONE and P07-M02-T002 READY; this checkpoint is recorded in `HANDOFF.md`.
- After the transition, the same bundled PowerShell validator reported `phases=19, milestones=41, tasks=119, ready=P07-M02-T002,P13-M01-T001`; `git diff --check` exited 0; canonical `docs/sources/FINAL_TECH_SPEC.md` SHA-256 remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Scope: changes add `TradingBot.Core` evidence contracts/snapshot, focused Core tests, and the listed documentation. Existing T001/T002/T003 contracts, evaluators, tests, and architecture notes were not modified. No persistence, SQLite schema, lifecycle, decision, risk, order, execution, package, project-graph, or canonical-source change was made. No commit or push was performed. `P07-M02-T002` is only READY and was not started; `P13-M01-T001` remains READY and was not started; blocked tasks were not touched. Remote CI remains unverified.

## P07-M02-T002 evidence

- Startup: clean worktree at `94c1331`; P07-M02-T001 was DONE, and P07-M02-T002 was the first READY task; P13-M01-T001 was also READY.
- Added `LifecycleInputFixtures.cs` and `LifecycleInputFixtureTests.cs` under Core.Tests. The hand-specified 17-case matrix covers Boundary Rejection and Center Retest, both directions, Ready/NoSetup/Invalidated/UnknownQuality and warmup without a signal bar. Expected states are independent of the observed evidence state.
- Tests replay and round-trip each evidence snapshot, checking equality, stable identity, causal cursor/source/instrument, sorted invalidation reasons and signal-bar presence. Cursor-prefix replay verifies that earlier evidence stays non-ready despite future history, while fixture evidence demonstrates later Ready; repeated evidence replay is stable without modeling lifecycle deduplication.
- Reflection recursively inspects evidence properties, collection element types and method return types for lifecycle/Decision/RiskDecision/OrderIntent/Position surfaces; strict JSON rejects injected `orderIntent` and `authorized` fields. This proves the evidence contract boundary and does not instantiate an executor or lifecycle state machine.
- Focused Release Core.Tests (`LifecycleInputFixtureTests|SetupEvidenceSnapshotTests|LaneASetupFixtureTests`, `TreatWarningsAsErrors=true`) passed 18/18; scoped format passed; `tools/validate-docs.ps1` passed with `ready=P08-M01-T001,P13-M01-T001`; `git diff --check` passed; canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Scope is limited to Core.Tests fixtures/tests and TASKS/HANDOFF docs. No Core production code, lifecycle, persistence, decision, risk, order, or execution behavior changed. Remote CI remains unverified.

## P08-M01-T001/T002/T003 evidence

- Startup: the bundled validator selected `P08-M01-T001` as the first READY task after `P07-M02-T002`; `P13-M01-T001` remained independently READY. Existing P07 changes were preserved. The canonical source hash remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused lifecycle test compile initially failed with missing `SetupLifecycleConfiguration`, `SetupLifecycleIdentity`, `SetupLifecycleInput`, `SetupLifecycleSnapshot`, and related engine contracts. Core then adds BCL-only immutable lifecycle contracts and a pure `SetupLifecycleEngine` over explicit external facts. Inputs retain event/receive/availability times, stable sequence, stream/event identity, evidence, confirmation, external plan/order/position/exit identifiers, provenance, and rules/config versions. Identity fingerprints include instrument, setup family, direction, structural anchor, anchor time, and context version.
- T001 lifecycle evidence: legal transitions cover context, structure, setup evidence, confirmation, external plan, order/position/exit observations, cooldown, terminal, explicit reset, and recovery. Ready evidence advances one stage only; unknown evidence and invalid transitions fail closed. `SetupLifecycleTransition` records previous/current state and identity, canonical full input JSON, provenance, rules/config versions, reasons, and opaque shadow/outcome observability. `InstrumentLifecycleProcessor` serializes complete read-modify-write per instrument key, permits different instruments to proceed independently, and leaves the previous snapshot visible after a rejected commit.
- T002 Decision Engine evidence: `DecisionEngineConfiguration`, `DecisionEngineInput`, `DecisionEvidence`, and `DecisionEngineResult` define an explicit Allow/Shadow profile. Lane A must be Ready and visible at the lifecycle cursor; veto, unknown, stale, future, or conflicting evidence blocks; required Lane B/C evidence must confirm unless an explicit neutral flag permits it; only `EntryArmed` without recovery is eligible for Allow or Shadow. The result contains the existing `Decision` plus the immutable input snapshot and has no RiskDecision, OrderIntent, Position, sizing, or execution surface.
- T003 idempotency/recovery evidence: exact stream/event identity repeats return `Duplicate` without a new receipt; conflicting content returns `IdentityConflict`, suspends the snapshot, and requires explicit recovery; late inputs return `Late` without state/cursor rollback; `Replay` reproduces the ordered transition list; recovery accepts a serialized snapshot; and commit rejection leaves the prior state. Focused `SetupLifecycleTests` passed 12/12, the combined lifecycle/evidence fixture filter passed 30/30, full Core.Tests passed 207/207, Core Release build with `--warnaserror` passed with zero warnings/errors, scoped format passed, and strict JSON round-trip/unknown-field, duplicate/conflict/late, same-instrument barrier, separate-instrument, Decision rule-table, and recovery fixtures passed.
- Architecture notes are [`docs/architecture/setup-lifecycle.md`](docs/architecture/setup-lifecycle.md) and [`docs/architecture/decision-engine.md`](docs/architecture/decision-engine.md). Scope is limited to Core lifecycle/decision contracts, Core tests, architecture notes, TASKS, and HANDOFF. No SQLite persistence, P08-M02 projections/reason codes, replay runner, Risk Engine, order execution, P09, P13, package graph, or canonical source changes were made. No commit or push was performed; remote CI remains unverified.

## P08-M02-T001 evidence

- Startup: the bundled validator selected `P08-M02-T001` as the first READY task; `P08-M01-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source hash remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: focused Core and Infrastructure projection tests initially failed to compile because `DecisionProjectionSnapshot` and `SqliteDecisionProjectionStore` were absent. GREEN adds the immutable Core projection and the SQLite store.
- Projection evidence: format-one `DecisionProjectionSnapshot` retains the complete Decision Engine result, both lifecycle/decision configurations, explicit `decision-reasons-v1`, and a lifecycle checkpoint exposing identity, causal cursor, availability time, receipts, latest evidence, diagnostics, and recovery state. Canonical lower-case SHA-256 identity excludes the identity property. Construction and read-back re-evaluate the saved decision and reject semantic drift.
- Persistence evidence: `SqliteDecisionProjectionStore.Open/Append/Find` uses a BLOB-keyed `decision_projections` table, idempotent exact retry, conflicting identity and DecisionId rejection, canonical JSON/row metadata/identity validation, and schema 9 -> 10 migration. Existing event journal rows survive migration. ALLOW/BLOCK/SHADOW append/find/reopen fixtures passed 3/3; the affected Infrastructure migration/persistence filter passed 61/61; Core projection fixtures passed 4/4.
- Validation: Release tests with warnings as errors and scoped `dotnet format --verify-no-changes` passed for Core, Infrastructure, and the focused test files. `tools/validate-docs.ps1` and `git diff --check` passed after the DONE transition. No P09/P13 implementation, package, project-graph, or canonical-source change was made; no commit or push was performed. `P08-M02-T002` is now the first READY task and `P13-M01-T001` remains READY.

## P08-M02-T002 evidence

- Startup: the bundled validator selected `P08-M02-T002` as the first READY task; `P08-M02-T001` was DONE and `P13-M01-T001` remained READY. The canonical source hash remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused reason-code contract test failed to compile because `DecisionReasonCodes` and `DecisionReasonCodeDefinition` were absent. GREEN adds the immutable, serializable catalog and switches Decision Engine emission to catalog constants/helpers without changing literal values, order, or duplicates.
- Contract evidence: `decision-reasons-v1` exposes stable constants, ordered `Codes`, auditable `Definitions`, JSON round-trip, lane mapping helpers, `RequireVersion`, and `RequireKnown`. Projection construction now rejects unknown reason versions and codes; persisted reads retain the same fail-closed behavior.
- Validation: focused reason/projection/lifecycle Core tests passed 20/20 with Release warnings-as-errors; projection store tests passed 6/6. Scoped format verification passed; `tools/validate-docs.ps1` and `git diff --check` passed after the DONE transition. `P08-M02-T003` is now READY and `P13-M01-T001` remains READY. No replay runner, P09/P13 implementation, package, project-graph, or canonical-source change was made; no commit or push was performed.

## P08-M02-T003 evidence

- Startup: the bundled validator selected `P08-M02-T003` as the first READY task; `P08-M02-T002` was DONE and `P13-M01-T001` remained READY. The canonical source hash remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Recovery fixture evidence: `LifecycleRecoveryFixtures` builds an equal-timestamp causal prefix, saves its complete lifecycle checkpoint in a decision projection, closes/reopens SQLite, then feeds duplicate, late/out-of-order, identity-conflict, and explicit Recovery inputs to both branches. The continuous `SetupLifecycleEngine.Replay` branch receives the identical sequence; every tail transition status/canonical JSON and per-step Decision Engine JSON/reasons match, and final identity/state/cursor/availability/receipts/diagnostics/recovery state are equal.
- Failure evidence: a held SQLite writer rejects the checkpoint append with `SqliteException`, leaves no partial projection row, and allows the same append after rollback. No second replay runner or event reordering was added.
- Validation: focused Recovery + projection Release tests with warnings-as-errors passed 8/8; scoped format verification passed; `tools/validate-docs.ps1` and `git diff --check` passed after the DONE transition. `P09-M01-T001` and `P13-M01-T001` are now READY. No P09/P13 implementation, package, project-graph, or canonical-source change was made; no commit or push was performed.

## P09-M01-T001 evidence

- Startup: the validator-selected first READY task was `P09-M01-T001`; `P08-M02-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Replay.Tests build first failed with missing `ReplayRunner`, `HistoricalReplayRunner`, `ReplayRunResult`, and diagnostic contracts. GREEN adds `ReplayRunner` and immutable context/result/diagnostic types in Backtesting plus `HistoricalReplayRunner` one-shot forwarding.
- Runner evidence: one stream is validated before delivery; exact duplicate envelopes are skipped with diagnostics; identity and stable-sequence conflicts stop the run. The virtual clock is `max(EventTime, AvailabilityTime, Bar.CloseTime)`, with event time and stable sequence tie ordering. Each handler receives an existing cursor-filtered immutable prefix, preserving receive/exchange metadata. A handler exception commits no failing step and prevents later steps.
- Focused GREEN: `HistoricalReplayRunnerTests` passed 4/4; Release Backtesting/Replay build with `--warnaserror` passed with zero warnings/errors; scoped format verification and `git diff --check` passed. The affected Replay.Tests assembly filter returned four passing tests.
- Documentation: [`docs/architecture/event-model.md`](docs/architecture/event-model.md) records runner availability and failure semantics. `docs/TASKS.md` marks `P09-M01-T001` DONE and `P09-M01-T002` READY.
- Scope: changes are limited to Backtesting, Replay.Tests, the event-model architecture note, TASKS, and this handoff. No Core contract, execution, package, project-graph, or canonical-source change was made; no commit or push was performed. `P09-M01-T002` is now the first READY task and will be rechecked before implementation.

## P09-M01-T002 evidence

- Startup: after T001, the validator reported `P09-M01-T002` as the first READY task with `P09-M01-T001` DONE; `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Replay.Tests build failed with missing `SimulatedRiskPlan`, `SimulatedTakeProfit`, and `SimulatedFillAssumptions`. GREEN adds the injected `SimulatedRiskSimulator`, immutable plan/assumption/fill/result contracts, and the `BacktestRiskSimulator` forwarding alias.
- Simulation evidence: `IRiskMathCalculator.Calculate` is called once and its versioned result supplies quantity and protective stop. Entry uses the first bar opened after the decision; same-bar exits are excluded. TP1/TP2 fractions, next-bar breakeven, explicit breakeven stop pricing with no entry-price fallback, SL-first same-bar ambiguity, adverse stop-gap opens, per-fill fee/slippage, time exit, end-of-data open remainder, and checked decimal overflow are covered. The plan now carries an explicit Core `Decision` link and fill assumptions carry an explicit `FillSimulationVersion`; missing links/versions block simulation. No order, authorization, or execution adapter surface is exposed.
- Focused GREEN: the final combined HistoricalReplayRunner/SimulatedRisk filter passed 10/10, including explicit-link/version hardening; Release Backtesting/Replay build with `--warnaserror` passed with zero warnings/errors; scoped format verification and `git diff --check` passed.
- Documentation: [`docs/architecture/risk-model.md`](docs/architecture/risk-model.md) records the injected risk boundary, explicit assumptions, and research-only results. `docs/TASKS.md` marks `P09-M01-T002` DONE and `P09-M01-T003` READY.
- Scope: changes are limited to Backtesting, Replay.Tests, risk-model/event-model architecture notes, TASKS, and this handoff. No Core contract, persistence, package, project-graph, or canonical-source change was made; no commit or push was performed. `P09-M01-T003` is now the first READY task and will be rechecked before implementation.

## P09-M01-T003 evidence

- Startup: after T002, the validator reported `P09-M01-T003` as the first READY task with `P09-M01-T002` DONE; `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Replay.Tests build failed with 14 expected missing report, comparison, limitation, configuration, and atomic-journal symbols. GREEN adds the versioned `BacktestReport`, deterministic `BacktestRegression`, explicit OHLCV limitation catalog, and temporary-file/flush/replace `BacktestReportJournal`.
- Regression evidence: reports retain causal event fields and Core payload JSON, setup identity/lifecycle transition references, Decision IDs/outcomes/reasons, injected risk results, simulated fills, costs, diagnostics, exit reason, and open remainder. Fingerprints exclude only RunId, so repeated runs compare equal across input permutations and Core JSON serialized/deserialized inputs while full canonical JSON remains auditable.
- Limitations evidence: every report states that OHLCV cannot reconstruct intrabar sequence or prove actual fills, spread, queue position, L2 state, or true point of control; TP portions remain management assumptions and liquidity/exchange partial fills are unproven. The report has no authorization, order, or execution surface.
- Focused GREEN: all scoped `TradingBot.Replay.Tests` Release tests passed 19/19; Backtesting/Replay Release build with `--warnaserror` passed with zero warnings/errors; scoped format verification and `git diff --check` passed.
- Documentation: [`docs/architecture/backtest-report.md`](docs/architecture/backtest-report.md) records the canonical report, comparison fingerprint, atomic export, retained causal references, and OHLCV evidence limits. `docs/TASKS.md` marks `P09-M01-T003` DONE.
- Scope: changes are limited to Backtesting, Replay.Tests, the report architecture note, TASKS, and this handoff. No Core contract, project graph, package, persistence, execution, P09-M02/P13-M01 work, or canonical-source change was made; no commit or push was performed. After validation, `P09-M02-T001` and `P13-M01-T001` are the expected READY tasks; implementation stopped at P09-M01.

## P09-M02-T001 evidence

- Startup: PowerShell 7 validator selected `P09-M02-T001` as the first READY task; `P09-M01-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Core.Tests build failed with the expected missing `LinearPerpetualRiskMathCalculator` type. The focused Replay.Tests parity build then failed its tampered-input assertion because the simulator checked only instrument, direction, and version.
- GREEN: Core adds BCL-only `LinearPerpetualRiskMathCalculator` version `linear-perpetual-risk-v1`. It uses checked decimal `riskBudget = equity * fraction`, `lossPerUnit = abs(entry - stop) + fee + slippage`, `quantity = budget / loss`, and `notional = entry * quantity`, with no venue rounding and explicit overflow/zero-precision rejection. `SimulatedRiskSimulator` now requires the complete returned `RiskMathInput` to equal the plan input and requires the injected calculator version.
- Evidence: independent LONG/SHORT vectors, high-precision decimal JSON round-trip, matching currency/stop guards, overflow/zero-precision behavior, direct planning calculation, replay simulation parity, and tampered full-input rejection passed. Core calculator tests passed 4/4; combined Replay parity and existing simulation tests passed 8/8. Release builds with warnings-as-errors passed with zero warnings/errors. Scoped include-only format checks and `git diff --check` passed; the PowerShell 7 docs validator passed with `ready=P09-M02-T002,P13-M01-T001`.
- Architecture: [`docs/architecture/risk-model.md`](docs/architecture/risk-model.md) records the formula, fixed version, decimal/venue boundary, parity evidence, and the fact that calculation parity does not establish profitability or replace the Risk Engine. `docs/TASKS.md` marks `P09-M02-T001` DONE and `P09-M02-T002` READY.
- Scope: changes are limited to the Core calculator, Backtesting input/version validation, focused Core/Replay tests, risk-model architecture note, TASKS, and this handoff. No fill simulator, execution authorization, package, project-graph, persistence, P09-M03/P13-M01 behavior, or canonical source change was made; no commit or push was performed.

## P09-M02-T002 evidence

- Startup: after T001, the PowerShell 7 validator selected `P09-M02-T002` as the first READY task; `P09-M02-T001` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Replay.Tests build failed with the expected missing two-argument `SimulatedRiskSimulator` constructor and missing `OhlcvFillSimulator` type.
- GREEN: `SimulatedRiskSimulator` retains its calculator-only constructor and adds an `IFillSimulator` overload. The default `OhlcvFillSimulator` (`ohlcv-fill-v1`) returns one reference-price observation per causal request. The harness retains trigger selection, TP/time sizing, numeric fee/slippage accounting, role assignment, causal event mapping, and research-only result construction. It rejects model result/input/version mismatches, overfills, future observations, and stable sequences outside the visible prefix. `SimulatedRiskSimulationResult` records the injected fill-model version.
- Evidence: replaceable model output changes the fill price while the `RiskMathResult` remains byte-for-byte equal to direct planning; the captured input excludes a future bar; invalid future observations fail closed; the public model surface contains no risk, order, authorization, or execution names. Focused isolation, existing simulation, and parity tests passed 11/11. Release Backtesting/Replay build with warnings-as-errors passed with zero warnings/errors; include-only format checks and `git diff --check` passed.
- Architecture: [`docs/architecture/risk-model.md`](docs/architecture/risk-model.md) records the model seam, causal-prefix validation, numeric-cost ownership, and research-only boundary. `docs/TASKS.md` marks `P09-M02-T002` DONE and `P09-M02-T003` READY.
- Scope: changes are limited to Backtesting simulation/fill injection, focused Replay tests, risk-model architecture note, TASKS, and this handoff. No execution authorization, persistence, package, project-graph, P09-M03/P13-M01 behavior, or canonical source change was made; no commit or push was performed.

## P09-M02-T003 evidence

- Startup: after T002, the PowerShell 7 validator selected `P09-M02-T003` as the first READY task; `P09-M02-T002` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused BacktestRegressionTests build failed because `BacktestReport` did not expose the injected `FillSimulationVersion` or serialize that model identity.
- GREEN: `BacktestReport` now retains the fill-model version beside the shared `RiskMathResult`, full `RiskPlan.FillAssumptions`, simulated costs, fills, causal references, diagnostics, and open remainder. Canonical serialization and the run-identity-independent fingerprint include the model version and assumptions. Existing atomic UTF-8 report replacement remains unchanged.
- Evidence: the report fixture proves the default `ohlcv-fill-v1` model and caller-supplied assumption version are both present in canonical JSON; five focused report tests passed. The complete affected Replay.Tests Release run passed 25/25; Backtesting/Replay build with warnings-as-errors passed with zero warnings/errors; include-only format checks, `git diff --check`, and the PowerShell 7 docs validator passed.
- Limitations: the report states that OHLCV cannot recover intrabar event sequence, actual fills, spread, queue position, Level 2, or true point of control. TP portions remain management assumptions; liquidity and exchange partial fills are unproven. Reproducibility and parity are engineering evidence only and do not establish profitability or live execution quality.
- Architecture: [`docs/architecture/risk-model.md`](docs/architecture/risk-model.md) and [`docs/architecture/backtest-report.md`](docs/architecture/backtest-report.md) record the version/assumption links, deterministic fingerprint, and evidence limits. `docs/TASKS.md` marks `P09-M02-T003` DONE.
- Scope: changes are limited to the Core calculator, Backtesting simulator/report, focused Core/Replay tests, risk/backtest architecture notes, TASKS, and this handoff. No P09-M03/P13-M01 implementation, execution authorization, persistence, package, project-graph, or canonical source change was made; no commit or push was performed.

## P09-M03-T001 evidence

- Startup: the PowerShell validator selected `P09-M03-T001` as the first READY task; `P09-M02-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD: the focused Replay.Tests build first failed with the expected missing `FrozenResearchManifest` type. GREEN adds the BCL-only immutable manifest, canonical causal event/configuration hashes, strict integrity restore, pre-run replay validation, write-once manifest journal, and immutable evaluation snapshot linked to the canonical report.
- Evidence: `ResearchFreezeTests` passed 4/4; the complete affected Replay.Tests assembly passed 29/29. Backtesting Release build with warnings-as-errors passed with zero warnings/errors; scoped format verification, `tools/validate-docs.ps1`, and `git diff --check` passed. Exact duplicate causal rows remain in the frozen manifest while the existing replay runner delivers them once by its established idempotent semantics.
- Architecture: [`docs/architecture/research-freeze.md`](docs/architecture/research-freeze.md) records the frozen fields, canonical ordering, integrity checks, pre-run binding, write-once journal, snapshot capture, and evidence limits. The fixtures are synthetic and do not establish real OOS provenance or profitability.
- Scope: changes are limited to Backtesting, Replay.Tests, the research-freeze architecture note, TASKS, and this handoff. No Core contracts, Infrastructure dependency, package, project graph, execution behavior, P13 behavior, or canonical source change was made; no commit or push was performed. `P09-M03-T002` is now the first READY task and must be rechecked before implementation.

## P09-M03-T002 evidence

- Startup: after T001, the PowerShell validator selected `P09-M03-T002` as the first READY task; `P09-M03-T001` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD: the focused criteria build first failed with the expected missing `PromotionCriterion`, `PredeclaredPromotionCriteria`, journal, and evaluation-context types. GREEN adds immutable manifest-bound criteria with stable IDs/questions/evidence, explicit unresolved blockers, strict canonical restore, a write-once journal, and a context that blocks replay/capture until matching criteria are registered and ready.
- Evidence: `PromotionCriteriaTests` passed 3/3; the complete affected Replay.Tests assembly passed 32/32. Release Backtesting and Replay builds with warnings-as-errors passed with zero warnings/errors; scoped format verification, `tools/validate-docs.ps1`, and `git diff --check` passed. No automatic promotion decision or numeric threshold was added.
- Architecture: [`docs/architecture/research-freeze.md`](docs/architecture/research-freeze.md) records the criteria identity, manifest binding, unresolved-blocker readiness gate, write-once registration, and evidence boundary.
- Scope: changes are limited to Backtesting, Replay.Tests, the research-freeze architecture note, TASKS, and this handoff. No Core contracts, Infrastructure dependency, package, project graph, execution behavior, P13 behavior, or canonical source change was made; no commit or push was performed. `P10-M01-T001` is now the first READY task; `P13-M01-T001` remains independently READY and implementation stopped at P09-M03.

## P10-M01-T001 evidence

- Startup: the validator selected `P10-M01-T001` as the first READY task; `P09-M03-T002` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Core.Tests build first failed with the expected missing `LaneBOpenInterestObservation` and `LaneBFundingObservation` contracts.
- GREEN: Core adds immutable `LaneBInput`, causal OI/funding observations that wrap the existing `OIUpdate`/`FundingUpdate`, explicit source/event identity, instrument, units, funding kind, provenance, event/receive/availability/exchange times, and stable sequence. `LaneBInput.Select` filters the shared causal prefix, preserves exact decimal values, computes only explicitly configured absolute OI deltas, and reports unknown configuration, missing, unavailable, incompatible, conflict, insufficient-history, duplicate, and overflow states without defaults.
- Focused GREEN: `LaneBInputTests` passed 4/4. The Core Release build with warnings-as-errors passed with zero warnings/errors; scoped format verification, `tools/validate-docs.ps1`, and `git diff --check` passed.
- Architecture: [`docs/architecture/lane-b.md`](docs/architecture/lane-b.md) records the causal input boundary, explicit unit/configuration rules, duplicate/conflict behavior, and synthetic-fixture limits.
- Scope: changes are limited to Core, Core.Tests, the Lane B architecture note, TASKS, and this handoff. No provider/Binance mapping, Decision handoff, persistence, package, project-graph, or canonical-source change was made; `P10-M01-T002` is now the first READY task and must be rechecked before implementation.

## P10-M01-T002 evidence

- Startup: after T001 the validator selected `P10-M01-T002` as the first READY task; `P10-M01-T001` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Core.Tests build first failed with the expected missing liquidation, failed-continuation, rule-table, evaluator, and shadow-result contracts.
- GREEN: Core adds standalone causal `LaneBLiquidationObservation` and externally supplied `LaneBFailedContinuationObservation` with method versions, outcomes, causal links, and provenance. `LaneBEvaluator` consumes only a caller-declared, setup/direction-specific metric table and returns `lane-b-shadow-v1` outcomes (`Confirm`, `Neutral`, `Veto`, `InsufficientData`) with reasons and used IDs. Missing liquidation coverage, unknown failed-continuation methods/links, missing rules, and OI-only confirmation remain insufficient; the evaluator has no Decision, RiskDecision, OrderIntent, or authorization surface.
- Focused GREEN: `LaneBShadowTests` passed 4/4. The Core Release build with warnings-as-errors passed with zero warnings/errors; scoped format verification, `tools/validate-docs.ps1`, and `git diff --check` passed.
- Architecture: [`docs/architecture/lane-b.md`](docs/architecture/lane-b.md) records standalone liquidation/behavior evidence, explicit rule-table metrics, shadow statuses, external failed-continuation limits, and the absence of a Decision handoff.
- Scope: changes remain limited to Core, Core.Tests, the Lane B architecture note, TASKS, and this handoff. No Binance mapping, provider integration, persistence, package, project-graph, Decision handoff, or canonical-source change was made; `P10-M01-T003` is now the first READY task and must be rechecked before implementation.

## P10-M01-T003 evidence

- Startup: after T002 the validator selected `P10-M01-T003` as the first READY task; `P10-M01-T002` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused freshness/replay build first failed with the expected missing TTL policy, freshness evidence/evaluator, and Lane B replay fixture contracts.
- GREEN: Core adds caller-supplied per-series TTL policy with unknown/non-positive values blocked, independent event and availability ages with equality-at-TTL freshness, and `LaneBEvaluator` policy gating. Exact duplicates are retained once with diagnostics; same-identity conflicts block affected evidence; input units/sources and liquidation arithmetic remain explicit failures. Existing `HistoricalReplayRunner` feeds causal OI/funding prefixes to the same selection/evaluator, preserving late records and prior cursor results.
- Focused GREEN: `LaneBInputTests`, `LaneBShadowTests`, and `LaneBFreshnessTests` passed 13/13; `LaneBReplayTests` passed 4/4. Core and Replay Release builds with warnings-as-errors passed with zero warnings/errors; scoped format verification, `tools/validate-docs.ps1`, and `git diff --check` passed.
- Architecture: [`docs/architecture/lane-b.md`](docs/architecture/lane-b.md) records the caller TTL boundary, independent ages, no-lookahead replay evidence, duplicate/conflict handling, and freshness limitations.
- Scope: changes are limited to Core, Core.Tests, Replay.Tests, the Lane B architecture note, TASKS, and this handoff. No provider/Binance mapping, persistent causal join, Decision handoff, package, project-graph, or canonical-source change was made. After validation the expected READY tasks are `P10-M02-T001` and `P13-M01-T001`; implementation stops at P10-M01.

## P10-M02-T001 evidence

- Startup: after P10-M01-T003, the PowerShell 7 validator selected `P10-M02-T001` as the first READY task; `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Core.Tests build failed with the expected missing `LaneBQualityState`, `LaneBQualityStatus`, and `LaneBQualityEvaluator` symbols. No production implementation existed before the test.
- GREEN: Core adds immutable `LaneBQualityState` format 1 / `lane-b-quality-v1` and a pure evaluator over the existing `LaneBShadowResult`. It retains the causal cursor, sorted observation identities, caller-owned freshness evidence, and stable reasons. Missing, stale, unavailable, conflict, incompatible, arithmetic-overflow, and unknown states stay distinct; a missing freshness policy is unknown and cannot produce ready quality.
- Focused GREEN: direct xUnit DLL execution of `TradingBot.Core.Tests.LaneBQualityTests` passed 5/5. The fixtures cover absent policy, missing/unavailable, stale/conflict/overflow, cursor incompatibility, strict JSON round trip, and unknown-field rejection.
- Verification: `C:\temp\dotnet10\dotnet.exe build tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --warnaserror` passed with zero warnings/errors; scoped `dotnet format --verify-no-changes` passed; `tools/validate-docs.ps1` passed; `git diff --check` passed.
- Scope: changes are limited to Core, Core.Tests, the Lane B architecture note, TASKS, and this handoff. No provider mapping, persistence, replay-runner, Decision handoff, package, project-graph, P13, or canonical-source change was made. `P10-M02-T002` is now the first READY task and must be rechecked before implementation.

## P10-M02-T002 evidence

- Startup: after P10-M02-T001, the PowerShell 7 validator selected `P10-M02-T002` as the first READY task; `P13-M01-T001` remained independently READY. T001 was DONE and the canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Replay.Tests build failed with the expected missing `LaneBJoinSnapshot` symbol. No join implementation existed before the test.
- GREEN: Core adds immutable `LaneBObservationReference` and `LaneBJoinSnapshot` (`lane-b-join-v1`, format 1). The join filters event/availability/stable-sequence eligibility before deterministic ordering, preserves event/exchange/receive/availability/source/provenance/stable sequence and canonical payload identity, collapses exact duplicates, and retains conflicting same-identity rows while changing quality to `Conflict`. The existing `HistoricalReplayRunner` is unchanged.
- Focused GREEN: direct xUnit DLL execution of `TradingBot.Replay.Tests.LaneBJoinTests` passed 6/6. Fixtures cover permutations, equal timestamps, exact duplicates, conflicts, future/delayed prefix filtering, runner integration, and strict JSON round trip.
- Verification: Core and Replay Release builds with `--warnaserror` passed with zero warnings/errors; scoped `dotnet format --verify-no-changes` passed; `tools/validate-docs.ps1` passed; `git diff --check` passed.
- Scope: changes are limited to Core, Replay.Tests, the Lane B architecture note, TASKS, and this handoff. No MarketEvent or replay-runner change, provider mapping, persistence, Decision handoff, package, project-graph, P13, or canonical-source change was made. `P10-M02-T003` is now the first READY task and must be rechecked before implementation.

## P10-M02-T003 evidence

- Startup: after P10-M02-T002, the PowerShell 7 validator selected `P10-M02-T003` as the first READY task; `P13-M01-T001` remained independently READY. T001 and T002 were DONE and the canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Core.Tests build failed with the expected missing `DecisionEvidence.FromLaneBJoin`, `LaneBDecisionEvidenceMetadata`, optional metadata property, and seven-argument constructor symbols. No handoff implementation existed before the test.
- GREEN: Core adds typed optional `LaneBDecisionEvidenceMetadata` (`lane-b-decision-evidence-v1`, format 1) and validated `DecisionEvidence.FromLaneBJoin`. Metadata retains the causal join identity, shadow evidence identity/version/status, quality status/reasons, cursor, and observation references. Unready quality maps to existing `DecisionEvidenceStatus.Unknown`; cursor, identity, algorithm-version, lane, and status mismatches fail closed. Null metadata is omitted from JSON so legacy projection canonical JSON and identities remain unchanged.
- Focused GREEN: direct xUnit DLL execution of `TradingBot.Core.Tests.DecisionEvidenceMetadataTests` passed 5/5. Fixtures cover ready/missing/stale/conflict visibility, strict metadata round trip, inconsistent metadata rejection, lifecycle/recovery blocking, deterministic traces, and absence of risk/order/authorization surfaces.
- Adjacent regression evidence: direct xUnit DLL execution passed `DecisionProjectionSnapshotTests` 4/4, `SetupLifecycleTests` 12/12, and existing `LaneBReplayTests` 4/4. Core and Replay Release builds with `--warnaserror` passed with zero warnings/errors; scoped `dotnet format --verify-no-changes`, `tools/validate-docs.ps1`, and `git diff --check` passed. One concurrent Core/Replay build attempt hit a shared-output file lock; the required sequential Core rebuild and Replay build both passed.
- Scope: changes are limited to Core, Core.Tests, Replay.Tests, Lane B/Decision architecture notes, TASKS, and this handoff. No RiskDecision, OrderIntent, authorization, MarketEvent, replay-runner, provider, persistence, package, project-graph, P13, or canonical-source change was made. `P11-M01-T001` and `P13-M01-T001` are now the expected READY tasks; implementation stops at P10-M02.

## P11-M02-T001 evidence

- Startup: the PowerShell 7 validator selected `P11-M02-T001` as the first READY task; `P11-M01-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused risk-reservation build first failed because the unauthenticated two-argument reservation API and risk-evidence read API were absent. A compile-only shell then produced 5/5 focused runtime failures from the unimplemented methods, with the test class discovered and executing.
- GREEN: `SqlitePortfolioReservationStore.TryReserve` now requires the complete `RiskEvaluationResult`, expected reconciliation revision, and shared `IRiskMathCalculator`; it recomputes and canonically compares evidence, requires approval and matching setup/portfolio/instrument/direction/quantity/risk/exposure/slot/configuration identities, rereads totals under one immediate transaction, and atomically stores intent, reservation, and risk evidence. Repeated identical calls return `AlreadyReserved`; conflicts, stale/unresolved acknowledgement, missing portfolios, capacity overflow, and calculator/evidence failures leave no partial rows. Legacy reservations remain counted and cannot be silently reauthorized. Schema 10 -> 11 adds the durable `risk_evaluations` table and restart read API.
- Focused GREEN: `SqliteRiskReservationTests` passed 6/6, `SqlitePortfolioReservationStoreTests` passed 11/11, and `SqliteOperationalStateTests` passed 6/6. The Infrastructure.Tests Release build with `--warnaserror` passed with zero warnings/errors.
- Documentation: `docs/architecture/persistence.md`, `docs/architecture/execution-model.md`, and `docs/architecture/risk-model.md` record the authenticated reservation boundary and schema migration. `docs/TASKS.md` marks `P11-M02-T001` DONE and `P11-M02-T002` READY. The final scoped format, validator, and diff checks passed on the combined P11-M02 changes after T003; they were not run as a separate T001 exit check.
- Scope: no Core contract, project graph, package, order submission, execution adapter, `graft/`, or canonical source change was made; no commit or push was performed. T002 must recheck task/dependency state and the validator before adding tests.

## P11-M02-T002 evidence

- Startup: after T001, the PowerShell 7 validator selected `P11-M02-T002` as the first READY task; `P11-M02-T001` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: focused operational tests were written before the implementation change. The Release build passed, and the focused run discovered 9 tests with 1 runtime failure: `ReleaseRequiresAnAssociatedPositionSnapshot` released a reservation when the checkpoint was closed but no linked position snapshot existed. The failure was in runtime behavior, not setup or test discovery.
- GREEN: `RecordObservation` evidence now has explicit exact persistence/reopen coverage for quantity, price, nullable fee, event/exchange/receive/availability times, and stable sequence. Sequential partial fills retain the full reservation and capacity totals; duplicate observations remain idempotent; identity conflicts, overfill, stale revisions, and checked quantity overflow leave no partial rows. Release now requires the current acknowledged checkpoint, a closed intent, and at least one latest associated closed position snapshot; a closed intent alone or a closed snapshot with an open intent fails. Repeated release is idempotent and saved risk evidence remains after release.
- Focused GREEN: `SqliteOperationalStateTests` passed 9/9 after the minimum guard. The affected Infrastructure.Tests Release build with `--warnaserror` passed with zero warnings/errors.
- Documentation: persistence and execution architecture notes now record the operational durability and release conditions. `docs/TASKS.md` marks `P11-M02-T002` DONE and `P11-M02-T003` READY. The final scoped format, validator, and diff checks passed on the combined P11-M02 changes after T003; they were not run as a separate T002 exit check.
- Scope: no Core contract, project graph, package, order submission, execution adapter, `graft/`, or canonical source change was made; no commit or push was performed. T003 must recheck task/dependency state and the validator before adding tests.

## P11-M02-T003 evidence

- Startup: after T002, the PowerShell 7 validator selected `P11-M02-T003` as the first READY task; `P11-M02-T002` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: focused operational tests were written before the implementation change. The Release build passed, and the focused run discovered 14 tests with 4 runtime failures: acknowledgement accepted checkpoint unknown/manual markers and latest actual manual/unknown position origins, and the recovery fixture could not observe the required rejection. The inconsistent-link rollback expectation was corrected to the already acknowledged revision-0 state and was not counted as a product failure.
- GREEN: acknowledgement now refuses checkpoint markers and latest actual `Manual`/`Unknown` position origins inside its immediate transaction, even when checkpoint markers omit them. `CanRetry`, new reservation validation, and release use the same latest-origin guard in their transactions; observations reset acknowledgement, stale acknowledgements fail, and an empty portfolio requires explicit revision-zero acknowledgement. Recovery after a later known snapshot and explicit reconciliation persists across restart. Known open positions continue to retain their reservation capacity.
- Focused GREEN: `SqliteOperationalStateTests` passed 14/14; `SqlitePortfolioReservationStoreTests` passed 11/11; `SqliteRiskReservationTests` passed 6/6. The complete affected Infrastructure.Tests Release run passed 89/89. The Release build with `--warnaserror`, scoped format verification, `tools/validate-docs.ps1`, and `git diff --check` passed.
- Documentation: persistence, execution, and risk-model architecture notes now record the schema-11 risk evidence, authenticated reservation boundary, partial-fill/release conditions, and latest-origin reconciliation guard. `docs/TASKS.md` marks `P11-M02-T003` DONE and exposes `P12-M01-T001` plus `P13-M01-T001` as READY.
- Scope: no Core contract, project graph, package, order submission, execution adapter, `graft/`, or canonical source change was made; no commit or push was performed. Implementation stops after the P11-M02 block; the next READY tasks are confirmed by the final validator.

## P12-M01-T001 evidence

- Startup: the bundled PowerShell validator selected `P12-M01-T001` as the first READY task; `P11-M02-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Official protocol review: on 2026-10-24, the current Binance USDⓈ-M Trade, Account, and User Data Streams documentation was rechecked for signed/API-key authentication, timestamp and `recvWindow` limits, order/Algo paths and fields, error classes including HTTP 503 unknown outcomes, order-history retention, stream event envelopes, and position freshness. Unknown freshness policy and undocumented nested Algo fields remain explicit unknowns; no endpoint or credential was used.
- TDD RED: after adding the profile fixtures and a compile-only contract shell, the direct Release DLL run discovered 9 tests and failed 9 at runtime with `NotImplementedException` from the absent adapter behavior. The test class was present, the DLL executed, and the selection was non-zero.
- GREEN: `IBinancePrivateRestClient`, signed REST/API-key stream operations, exact-decimal order/trade/position DTOs, One-way/BOTH fail-closed mode checks, injected private stream parser/transport, causal metadata, delta `ACCOUNT_UPDATE`, fill mapping, opaque `ALGO_UPDATE`, bounded responses, and secret-safe unknown-outcome errors are implemented. Focused `BinancePrivateAdapterTests` passed 12/12; full `TradingBot.Exchange.Binance.Tests` passed 50/50. The Release test build with `--warnaserror` passed with 0 warnings and 0 errors; scoped format verification passed; docs validation and `git diff --check` passed after this transition.
- Changed files: `src/TradingBot.Exchange.Binance/BinancePrivateContracts.cs`, `BinancePrivateRestClient.cs`, `BinancePrivateStreamParser.cs`, `BinancePrivateWebSocketClient.cs`, `tests/TradingBot.Exchange.Binance.Tests/BinancePrivateAdapterTests.cs`, `docs/architecture/private-protocol.md`, `docs/architecture/execution-model.md`, `docs/TASKS.md`, and this handoff.
- Scope and limits: no Core contract, SQLite schema, durable execution state, real/private network request, production activation, package, project-graph, `graft/`, canonical source, commit, or push was changed. Hedge/unknown position mode, unknown Algo operations, unknown outcomes, and missing freshness evidence remain blocked until later reconciliation work.
- After validation, `P12-M01-T002` is READY and `P13-M01-T001` remains independently READY. T002 must recheck task/dependency state and the validator before adding tests; no Sol post-audit was run.

## P12-M01-T002 evidence

- Startup: after T001, the PowerShell validator was re-run and selected
  `P12-M01-T002` as the first READY task; `P12-M01-T001` was DONE and
  `P13-M01-T001` remained independently READY. No user changes, `graft/`,
  canonical source, package, or project-graph files were altered.
- TDD RED: focused Release DLLs were compiled with compile-only shells and
  executed directly with the required xUnit runner. Core discovered 6 and
  failed 6 at runtime with `NotImplementedException`; Infrastructure
  discovered 5 and failed 5 at runtime; App coordinator integration
  discovered 2 and failed 2 at runtime. No RED relied on a missing class,
  compile failure, or zero-test selection.
- GREEN: Core adds BCL-only deterministic lifecycle states and observations for
  accepted, rejected, partial, filled, cancel-requested, cancelled, timeout,
  unknown, and conflict outcomes. Exact receipts are idempotent; conflicting
  receipts, overfills, terminal mutations, timeout, and unknown outcomes fail
  closed. `ExecutionStateMachineTests` passed 6/6 and full Core.Tests passed
  277/277.
- GREEN: Infrastructure adds SQLite schema migration 11 -> 12 with durable
  `execution_states`, `execution_attempts`, and `execution_receipts`. The new
  `SqlitePortfolioOperationalStore` atomically verifies the acknowledged
  reconciliation revision, creates a deterministic dispatch claim, persists
  observations and receipts, and reconstructs state after restart. Sequential
  and concurrent duplicate claims cannot create a second attempt; unknown and
  conflicting receipts remain reconciliation-required. Focused execution
  tests passed 6/6 and full Infrastructure.Tests passed 95/95.
- GREEN: App `ExecutionCoordinator` requires an approved existing
  `RiskEvaluationResult`, invokes the existing atomic reservation/intent store,
  then dispatches only after a durable `Claimed` result. `AlreadyClaimed` is
  blocked without another adapter call; timeout and adapter exceptions become
  durable timeout/unknown observations. Focused coordinator tests passed 2/2
  and full IntegrationTests passed 8/8. The adapter remains injected and no
  private network or real order is reachable from these fixtures.
- Verification: affected Core.Tests, Infrastructure.Tests, and
  IntegrationTests Release builds with `--warnaserror` passed with zero
  warnings/errors; scoped `dotnet format --verify-no-changes`,
  `tools/validate-docs.ps1`, and `git diff --check` passed. No commit, push,
  production activation, or automatic Sol audit was performed.
- Changed files: `src/TradingBot.Core/ExecutionLifecycleContracts.cs`,
  `tests/TradingBot.Core.Tests/ExecutionStateMachineTests.cs`,
  `src/TradingBot.Infrastructure/SqliteDatabase.cs`,
  `src/TradingBot.Infrastructure/SqliteExecutionStateStore.cs`,
  `tests/TradingBot.Infrastructure.Tests/SqliteExecutionStateTests.cs`,
  affected schema-version assertions in Infrastructure.Tests,
  `src/TradingBot.App/ExecutionCoordinator.cs`,
  `tests/TradingBot.IntegrationTests/ExecutionCoordinatorTests.cs`,
  `docs/architecture/execution-model.md`,
  `docs/architecture/persistence.md`, `docs/TASKS.md`, and this handoff.
- After validation, `P12-M01-T003` is the first READY task and
  `P13-M01-T001` remains independently READY. T003 must recheck status,
  dependencies, and the validator before adding restart-reconciliation tests;
  implementation stops at T002 until that check.

## P12-M01-T003 evidence

- Startup: after T002, the validator was re-run before implementation and
  selected `P12-M01-T003` as the first READY task; `P12-M01-T002` was DONE and
  `P13-M01-T001` remained independently READY. No user changes, `graft/`,
  canonical source, package, or project-graph files were altered.
- Official protocol boundary retained: recovery uses only injected adapter
  evidence. It does not call Binance, submit an order, retain credentials, or
  activate production/private execution. Position mode remains One-way/BOTH;
  unknown and Hedge modes stay blocked.
- TDD RED: the compile-only recovery shell built successfully, then the direct
  Release DLL discovered 5 tests and failed all 5 at runtime with
  `NotImplementedException`. The failures covered restart/no-order retry,
  limited-history absence, duplicate post-commit evidence, partial-fill/cancel
  ordering, and an injected interruption after the external result. No missing
  class, compile failure, or zero-test selection was used as RED.
- GREEN: Core adds explicit recovery evidence status, completeness/history flags,
  and exact fill/position/reservation identity sets. The Infrastructure facade
  reconstructs fills and local operational facts, binds replay to the current
  acknowledged revision, and authorizes retry only after complete fresh no-order
  evidence. The App coordinator replays reconciliation observations idempotently
  and blocks stale, unknown, manual, conflicting, incomplete, limited-history,
  or identity-mismatched evidence. Durable facts are checked before retry and a
  fresh no-order recovery advances the dispatch attempt only after explicit
  recovery.
- Focused GREEN: `ExecutionRecoveryTests` passed 7/7; Core recovery tests 3/3;
  related Core state-machine tests 6/6; Infrastructure execution tests 6/6,
  operational regressions 14/14, and reservation regressions 11/11; coordinator
  regressions 2/2. The affected Core, Infrastructure, App, Core.Tests,
  Infrastructure.Tests, and IntegrationTests Release builds with
  `--warnaserror` passed with zero warnings/errors. Scoped format write and
  `--verify-no-changes`, `tools/validate-docs.ps1`, and `git diff --check` passed.
- Changed files: `src/TradingBot.Core/ExecutionRecoveryContracts.cs`,
  `src/TradingBot.Infrastructure/SqliteExecutionStateStore.cs`,
  `src/TradingBot.App/ExecutionCoordinator.cs`,
  `tests/TradingBot.Core.Tests/ExecutionRecoveryStateTests.cs`,
  `tests/TradingBot.IntegrationTests/ExecutionRecoveryTests.cs`,
  `docs/TASKS.md`, `docs/architecture/execution-model.md`,
  `docs/architecture/persistence.md`, and this handoff. Earlier T001/T002 files
  remain part of the uncommitted P12-M01 working tree.
- Scope: no commit, push, Sol post-audit, real/private network request, order
  submission, production activation, package, project-graph, canonical source,
  or `graft/` change was made. The next READY tasks after final validation are
  `P12-M02-T001` and the independent `P13-M01-T001`; implementation stops here.

## P12-M02-T001 evidence

- Startup: the required project documents, clean working-tree status, and
  `tools/validate-docs.ps1` were rechecked. `P12-M02-T001` was the first READY
  task with `P12-M01-T003` DONE; `P13-M01-T001` remained independently READY.
- TDD RED: the focused Release IntegrationTests assembly built successfully,
  then the direct xUnit DLL run discovered 4 tests and failed all 4 at runtime
  because the development harness and durable single-use claim behavior were
  absent. RED did not rely on a compile failure, missing test class, or zero
  tests.
- GREEN: App adds a sealed, programmatic-only
  `PrivateExecutionDevelopmentHarness` over the existing dispatcher and
  reconciler seams. It accepts only stores and scripted Core observations; it
  exposes no URI, HTTP transport, Binance client, credential, or activation
  input. Infrastructure migrates SQLite schema 12 -> 13 with the durable
  `development_dispatch_claims` table. The claim is exact and single-use,
  survives restart, requires a persisted intent and reservation, and rejects
  forged/conflicting claims before any scripted dispatcher call. The scripted
  reconciler remains synthetic and fail-closed when evidence is absent.
- GREEN evidence: `PrivateExecutionDevelopmentHarnessTests` passed 7/7;
  affected `Infrastructure.Tests` passed 95/95 and `IntegrationTests` passed
  22/22. Infrastructure, App, and IntegrationTests Release builds with
  `--warnaserror` passed with zero warnings/errors. Scoped format verification,
  `tools/validate-docs.ps1`, and `git diff --check` all passed.
- Scope: no private network request, credential, real order, production
  activation, package, project-graph, canonical source, `graft/`, commit, push,
  or automatic Sol audit was performed. The next READY task is
  `P12-M02-T002`; status, dependencies, and the validator must be rechecked
  before its TDD cycle begins.

## P12-M02-T002 evidence

- Startup: after T001, the task table and validator were rechecked. The first
  READY task was `P12-M02-T002`, its dependency `P12-M02-T001` was DONE, and
  `P13-M01-T001` remained independently READY.
- TDD RED: the focused Release IntegrationTests DLL discovered 5
  `ProtectiveExecutionTests` and failed all 5 at runtime because the protective
  request and claim path were absent. The focused Exchange.Binance.Tests DLL
  discovered 4 `ReduceOnlyOrderMappingTests` and failed all 4 at runtime because
  the protective mapper was absent. Both DLLs compiled successfully; neither RED
  used a missing test class, compile failure, or zero-test selection.
- GREEN: Core adds `ProtectiveExecutionRequest`, which requires a Stop,
  TakeProfit, or Emergency role, `ReduceOnly`, a known position owned by the
  source intent, matching instrument, opposite direction, and quantity within
  the known position. Infrastructure advances SQLite schema 13 -> 14 with an
  atomic `protective_dispatch_claims` record bound to the durable execution
  attempt, position, source intent, and expected acknowledged revision. App
  obtains protective claims without entry risk or reservation capacity. The
  Binance mapper emits only One-way/BOTH, opposite-side, reduce-only mappings:
  Stop/TakeProfit use Algo market types and Emergency uses a market order; Hedge
  and unknown modes fail closed.
- GREEN evidence: `ProtectiveExecutionTests` passed 6/6 and
  `ReduceOnlyOrderMappingTests` passed 4/4. Affected Infrastructure.Tests
  passed 95/95, IntegrationTests 28/28, and Exchange.Binance.Tests 54/54.
  Core, Infrastructure, App, Exchange, and affected test Release builds with
  `--warnaserror` passed with zero warnings/errors. Scoped format verification,
  `tools/validate-docs.ps1`, and `git diff --check` passed.
- Scope: protective claims remain development/test composition only. No private
  network request, real order, production activation, credential, package,
  project-graph, canonical source, `graft/`, commit, push, or automatic Sol
  audit was performed. `P12-M02-T003` was READY and its status/dependency
  preflight plus validator were rechecked before the next TDD cycle.

## P12-M02-T003 evidence

- Startup: `P12-M02-T002` was DONE and `P12-M02-T003` was the first READY task;
  `P13-M01-T001` remained an independent READY task. The validator passed before
  tests and no unrelated block was started.
- TDD RED: the focused Release IntegrationTests assembly built successfully,
  then the direct xUnit DLL discovered 4
  `ActivationSeparationEvidenceTests` and failed all 4 at runtime because the
  activation-separation evidence builder was absent. RED did not use a missing
  test class, compile failure, or zero-test selection.
- GREEN: App adds `ActivationSeparationEvidence` and a static
  `ActivationSeparationEvidenceBuilder` that accepts only the sealed
  `PrivateExecutionDevelopmentHarness`, bounded provenance labels, a captured
  UTC timestamp, and a nonzero fully passing profile. The builder records a
  deterministic identity and scripted dispatch count while fixing private
  transport, credentials, and real-money activation facts closed; it exposes no
  activation method, endpoint, signer, or credential input. No SQLite schema or
  production composition change was added.
- GREEN evidence: `ActivationSeparationEvidenceTests` passed 4/4;
  `PrivateExecutionDevelopmentHarnessTests` passed 7/7,
  `ProtectiveExecutionTests` 6/6, and `ExecutionCoordinatorTests` 2/2.
  Affected App and IntegrationTests Release builds with `--warnaserror` passed
  with zero warnings/errors. Scoped format write and `--verify-no-changes`,
  `tools/validate-docs.ps1`, and `git diff --check` passed.
- Official protocol and prior-stage evidence remains unchanged: T001 RED was
  4/4 runtime failures and GREEN was 7/7; T002 recorded the official Binance
  private REST/user-stream boundary in the isolated adapter, migrated SQLite
  schema 13 -> 14 for protective claims, and passed its focused 6/6 and 4/4
  profiles plus affected regressions. T003 adds no private network call.
- Changed files for T003: `src/TradingBot.App/ActivationSeparationEvidenceBuilder.cs`,
  `tests/TradingBot.IntegrationTests/ActivationSeparationEvidenceTests.cs`,
  `docs/TASKS.md`, `docs/architecture/execution-model.md`,
  `docs/architecture/persistence.md`, and this handoff. Earlier P12-M02 files
  remain part of the uncommitted working tree.
- Scope: no commit, push, Sol post-audit, private network request, credential,
  real order, production activation, package, project-graph, canonical source,
  or `graft/` change was made. Final validation must leave
  `P13-M01-T001` as the next READY task; implementation stops here.

## P13-M01-T001 evidence

- Startup: the worktree was clean and `tools/validate-docs.ps1` confirmed
  `P13-M01-T001` READY with both dependencies DONE.
- TDD RED: the focused Release IntegrationTests DLL discovered 2
  `LaneCHistoryCaptureTests` and failed both at runtime because the Lane C
  capture pipeline and depth metadata were absent.
- GREEN: Core capture provenance now optionally retains typed Binance depth
  `U`/`u`/`pu`, recovery generation, snapshot identity, and the original
  per-session stable sequence. The Binance recovered-book state carries REST
  snapshot event/transaction times independently of the diff event that first
  aligns the book. App adds `LaneCHistoryCapturePipeline`, which assigns one
  durable capture/stable order to snapshots, public depth/trades, and source or
  recovery diagnostics; after a gap, depth capture remains closed until a new
  ready snapshot. Existing bounded Parquet persistence and old capture rows are
  preserved without a schema bump.
- GREEN and adjacent evidence: `LaneCHistoryCaptureTests` passed 3/3,
  `CaptureContractTests` 3/3, `ParquetCaptureStoreTests` 4/4, and
  `BinanceDepthAndOrderBookTests` 6/6. Release builds for Core.Tests,
  Infrastructure.Tests, Exchange.Binance.Tests, and IntegrationTests passed
  with zero warnings/errors. Scoped format verification, documentation
  validation, and `git diff --check` were run before marking the task DONE.
- Scope: no private network request, credential, order, execution path,
  package, project graph, canonical spec, `graft/`, commit, or push was used.
  The next READY task is P13-M01-T002; status, dependencies, and validator must
  be rechecked before its TDD cycle begins.

## P13-M01-T002 evidence

- Startup: after T001, the status/dependency preflight and documentation
  validator selected `P13-M01-T002` READY.
- TDD RED: the focused Release Core.Tests DLL discovered one
  `LaneCFeatureTests` case and failed at runtime because the Core evaluator was
  absent.
- GREEN: BCL-only Core adds versioned immutable Lane C input, configuration,
  per-price features, and results. The pure evaluator applies a bounded
  causal cut, reconstructs depth from snapshot/diff rows, checks Binance
  sequence continuity, and computes spread, top-N depth, book/aggressor
  imbalance, price-level changes, wall persistence/replenishment/withdrawal/
  migration candidates, and absorption candidates only under caller-supplied
  criteria. No feature threshold has a numeric default; absent evidence and
  thresholds remain explicit reasons/null results. Canonical input plus
  configuration determines a stable SHA-256 input identity.
- GREEN and adjacent evidence: `LaneCFeatureTests` passed 8/8, including
  hand-calculated decimal outputs, window boundaries, no-lookahead, stale book
  and trades, recovery gaps, warmup, zero denominator, wall candidates,
  explicit unknown thresholds, deterministic identity, JSON round-trip, and
  bounded input. `CoreBoundaryTests` passed 2/2, `ArchitectureTests` 1/1,
  `CaptureContractTests` 3/3, and `LaneCHistoryCaptureTests` 3/3. Core.Tests
  and IntegrationTests Release builds passed with zero warnings/errors; scoped
  format verification passed.
- Scope: no persistence, package, private network, execution, order, canonical
  source, `graft/`, commit, or push change was made. The next READY task is
  P13-M01-T003; status, dependencies, and validator must be rechecked before
  its TDD cycle begins.
