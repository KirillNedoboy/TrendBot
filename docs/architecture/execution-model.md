# Execution model

Execution is deferred to Phase 12 and remains disabled during bootstrap, shadow, and initial research. The future state machine must handle accepted, rejected, partially filled, cancelled, unknown, timed-out, and reconciled orders with idempotent client identities and native protective orders where supported.

## Local paper configuration (P16-M01-T001)

`LocalPaperConfiguration` is an immutable App contract with format version,
configuration identity, fixed `local-paper` mode, positive virtual initial
equity, explicit risk fraction, existing shared `RiskEvaluationConfiguration`,
and separately versioned `SimulatedFillAssumptions`. Unknown risk limits remain
unknown; equity, risk fraction, fees, slippage, and assumption descriptions have
no trading defaults.

`Parse`, `Load`, and `ToJson` use strict, case-sensitive JSON. Unknown members,
duplicate properties at any nesting level, missing required fields, unsupported
versions, and any mode other than `local-paper` fail closed. Core's existing
strict serializer validates nested monetary/risk contracts. Parser diagnostics
do not include source JSON or values. `Load` only reads its explicit file path;
it does not use environment overrides, create state, call a network, or compose
simulation or execution services. This contract exposes no credentials,
endpoints, activation field, order, or execution authority. T001 adds
configuration only; paper execution and restart/reconciliation evidence remain
T002/T003 work.

## Exchange test configuration (P16-M02-T001)

`ExchangeTestConfiguration` is an immutable, strict App contract for the
dedicated Binance USD-M test environment. It accepts only format 1,
`exchange-test`, and `binance-usdm-test`, with the documented test REST base
`https://demo-fapi.binance.com/` and private stream base
`wss://demo-fstream.binance.com/private/ws/`. Host, scheme, path, query,
fragment, and user-info validation reject production, arbitrary, and unsafe
endpoints. Configuration identities use safe metadata identifier characters and
cannot carry URLs, whitespace, or credential-like separators.

Credential fields are metadata references only. The provider is either
`environment` or `external-secret-store`, the scope must be
`binance-usdm-test`, and no secret value is resolved, stored, logged, or sent by
`Parse`, `Load`, or `ToJson`. Environment references do not carry a store
identity; external references require one. Strict case-sensitive JSON rejects
unknown or duplicate properties, unsupported versions, inline credential fields,
activation fields, and enabled production/network fixture policies. Operational
policy identifiers describe exchange rate limits, caller-owned freshness,
reconciliation-before-retry, reduce-only partial-fill protection, and
fail-closed unknown/manual state without adding numeric trading defaults.

T001 is configuration evidence only. It performs no network call, secret-store
lookup, order submission, or activation. T002 owns runtime credential resolution,
test transport, and private lifecycle behavior.

## Exchange test executor (P16-M02-T002)

The exchange-test runtime composes only `ExchangeTestConfiguration` with the
documented Binance USD-M test REST origin `https://demo-fapi.binance.com/` and
private stream origin `wss://demo-fstream.binance.com/private/ws/`. Credential
references are resolved at runtime by an environment or injected external
secret-store provider; values are held in-process only and are never represented
in configuration JSON, fixtures, diagnostics, SQLite receipts, or Git. The runtime
HTTP handler disables redirects, the REST adapter rejects a changed response
origin, and no production fallback or activation path exists.

`HmacSha256BinanceRequestSigner` signs the exact encoded payload with the runtime
secret. `BinancePrivateRestClient` appends `signature` as the final wire field,
retains the API-key header, applies caller-owned timestamp/freshness policy, and
maps rate-limit, authentication, and 503 unknown responses to redacted fail-closed
diagnostics. The private WebSocket default is the dedicated test `/private/ws/`
route; the reader retains event/transaction/receive/availability times, session
identity, and stable sequence. `ALGO_UPDATE` nested fields remain opaque and require
REST reconciliation.

`ExchangeTestRuntime.CreateTransport` and `CreateExecutor` compose the concrete
test-only `BinanceExchangeTestTransport` with the validated REST and private
WebSocket clients; the executor is not left dependent on a fixture-only fake.
Durable client order IDs are mapped unchanged into REST requests and matched back
to a fresh `ORDER_TRADE_UPDATE` before a submit result is accepted. The private
socket is connected before the REST order side effect, so an immediate update
cannot be lost by a post-submit subscription race. Reconciliation queries the
same client identity through private REST, checks one-way position mode and
current positions, requires bounded trade/order identities and a fresh matching
private event, and treats manual/unknown/mismatched state as non-retryable.
Stale, disconnected, expired, or mismatched stream evidence remains unknown and
fail-closed. `ALGO_UPDATE` remains opaque and blocks until dedicated Algo REST
reconciliation; no nested status is guessed.

`ExchangeTestExecutor` exposes asynchronous submit, reconciliation, and normalized
private-event recording over that transport. It revalidates approved risk, uses the
existing immediate SQLite reservation and single-use dispatch claim transactions,
and calls the transport only for a fresh durable claim. Duplicate or stale claims
are blocked. Timeout, cancellation, rate/auth, 503, redirect, and unknown transport
outcomes become durable reconciliation-required observations; absence from bounded
history never authorizes a retry. A partial entry fill creates a known snapshot
for the actual filled quantity and must place and REST-confirm a reduce-only
`STOP_MARKET` Algo order at the observed fill price before the result is safe;
failure becomes unknown state. Disconnected or stale private streams and
manual/unknown positions block new entries.

`SqliteExchangeTestStore` is intentionally separate from local-paper storage. Each
run is bound to one configuration/environment identity and persists only normalized
private-event receipts, causal timestamps, stable sequence, lifecycle, fill quantity,
and an integrity fingerprint. Raw frames, listen keys, signatures, credentials, and
credential-bearing response payloads have no storage representation. For an
exchange-test dispatch the exchange tables and portfolio operational tables must be
in the same isolated SQLite file: `ApplyExchangeTestObservation` uses one immediate
transaction for receipts, execution lifecycle, fills, position projections, and the
reconciliation checkpoint. A projection failure rolls back the complete batch, and
restart reads the committed batch idempotently; different database paths fail
closed before dispatch. Promotion/evidence projection is a later T003 concern and
this test lane does not authorize real-money execution. External Binance
connectivity remains unverified by the local T002 profile.

## Exchange-test evidence (P16-M02-T003)

`ExchangeTestEvidence` is a read-only, format-versioned projection of one
consistent persisted exchange-test snapshot. The snapshot reader uses one
serializable SQLite read transaction across run metadata, normalized private
receipts, lifecycle/attempt/receipt rows, fills, reservations, positions,
checkpoints, and protective links; a changed run identity or corrupt canonical
row fails closed. Evidence preserves run/configuration/environment identities,
causal event/exchange/receive/availability timestamps, stable sequence, fill
and reservation identities, restart/reconciliation/idempotency claims, and
explicit blockers/unknowns.

The canonical JSON is compact, strict, versioned, and content-addressed with
an uppercase SHA-256 identity. Deserialization recomputes both the identity and
canonical bytes and rejects tampering, unknown members, wrong environment, and
credential-bearing provenance metadata. The atomic exporter uses a flushed
exclusive temporary file and no-overwrite move; identical retries are
idempotent and conflicting destinations fail closed. Raw private frames,
listen keys, signatures, credentials, and credential-bearing payloads are not
part of the evidence model or export.

Synthetic-fixture, verified-test-environment, and unknown provenance remain
explicit. Local-paper evidence is never merged into this report. The promotion
projection is exclusively `ExchangeTest` and adds execution-authority and
real-money-activation blockers; it cannot authorize real orders. The local T003
profiles are synthetic persistence/fixture runs only, so external Binance
connectivity and verified test-environment evidence remain unprovided.

Core defines passive OrderIntent and Position contracts in Phase 2. P03-M02-T001
adds SqliteOrderIntentJournal in Infrastructure so a complete OrderIntent can be
durably recorded and recovered before any future execution side effect. This
state only means that an intent is awaiting future execution; it does not
submit or authorize an order.

P03-M02-T002 adds passive `PortfolioId`, `PortfolioLimits`, and
`PortfolioReservation` Core contracts. Limits and reservation amounts are
caller-supplied, non-negative, and monetary values within one portfolio use one
currency; no trading defaults are invented. `SqlitePortfolioReservationStore`
registers immutable limits. P11-M02-T001 replaces the unauthenticated reserve
call with `TryReserve(intent, reservation, riskEvaluation,
expectedReconciliationRevision, riskMathCalculator)`: it recomputes the
complete shared risk result, requires an approved canonical match to the
intent, setup, portfolio, instrument, direction, quantity, exposure, and slot
claim, then atomically persists the OrderIntent, reservation, and risk evidence
against the current acknowledged revision. It rereads capacity totals inside
the immediate serializable transaction and returns `Reserved`, `AlreadyReserved`,
or `InsufficientCapacity`. Legacy reservations remain counted but cannot be
silently reauthorized. This is a durable capacity boundary; it does not submit
or activate an order.

P03-M02-T003 adds passive operational `Fill`, `PortfolioPositionSnapshot`, and
`ReconciliationCheckpoint` contracts. A fill carries intent/instrument
identity, positive quantity and price, optional fee, event/receive/
availability/exchange timestamps, and stable sequence. Position snapshots
retain Core `Position` values and distinguish known intent-owned, manual, and
unknown state. `SqlitePortfolioReservationStore.RecordObservation` writes
observations and the monotonic portfolio revision together. Identical repeats
are idempotent; conflicts, aggregate fill overflow, stale revisions, and gaps
fail atomically. New observations clear acknowledgement. Acknowledgement is
refused while checkpoint markers or latest actual position snapshots contain
unresolved unknown/manual state. Retry remains closed until the current
revision is explicitly acknowledged. Partial fills retain the complete
reservation, and `ReleaseAfterReconciliation` releases it only after a current
acknowledged checkpoint confirms the order is closed and at least one linked
position snapshot is closed. The T002 operational boundary persists
exact fill quantity, price, nullable fee, event/exchange/receive/availability
times, and stable sequence across restart; duplicate observations are
idempotent, conflicts/overfill/checked overflow roll back, and release remains
idempotent while retaining the saved risk evidence. A closed intent alone or a
closed position snapshot alone cannot release a reservation.

The private Binance adapter is present at the exchange boundary for T001. It
contains only injected REST/WebSocket transport, signing, time, and parser seams;
it does not retain secrets, perform live calls in fixtures, submit an order, or
authorize execution. P12-M01-T002 adds BCL-only deterministic execution states
and observations for accepted, rejected, partial, filled, cancel-requested,
cancelled, timeout, unknown, and conflict outcomes. An exact receipt repeats
idempotently; a conflicting receipt, overfill, terminal mutation, timeout, or
unknown outcome stays fail-closed and requires reconciliation. Partial fills
remain associated with the durable reservation.

`SqlitePortfolioOperationalStore` composes the existing reservation and
operational store. It persists the intent state, dispatch attempts, and event
receipts in one schema-12 SQLite boundary. An immediate transaction checks the
explicit acknowledged portfolio revision and gives only one concurrent caller a
dispatch claim. The App coordinator requires an approved `RiskEvaluationResult`,
then lets the existing store atomically persist intent/reservation before the
durable claim; only a fresh `Claimed` result can reach an injected adapter. A
duplicate claim is blocked without a second adapter call. Adapter timeout and
unknown exceptions become durable reconciliation-required observations. No
private network, real order, production activation, or automatic Sol audit is
part of this boundary.

P12-M01-T003 adds restart recovery at the same boundary. `ExecutionRecoveryEvidence`
must identify the intent and current acknowledged reconciliation revision, declare
fresh complete order-history, position, and reservation knowledge, and carry exact
fill/position/reservation identities. The coordinator compares those identities
with durable SQLite facts before it can act. A fresh complete no-order result can
explicitly reset a reconciliation-required lifecycle to `IntentPersisted` for a
new dispatch attempt; absence from limited order history, stale or conflicting
revision, unknown/manual state, and identity mismatch remain blocked. Known order
observations are replayed in causal order, including repeated accepted events,
partial fills, and cancel races, with the existing receipt idempotency and
reservation guards. Failure-injection seams cover the durable claim, external
result, and observation-commit boundaries. Recovery fixtures use only artificial
transport/evidence and do not authorize blind retries or real execution.

P12-M02-T001 adds the development-only seam used to exercise this lifecycle.
`PrivateExecutionDevelopmentHarness` is a sealed App composition object over the
existing `IPrivateOrderDispatcher` and `IPrivateOrderReconciler` interfaces. Its
constructor accepts a portfolio store and scripted Core observations only; it
has no URI, HTTP client, Binance adapter, credential, or activation parameter.
The harness requires the intent and reservation to be durable before a scripted
dispatch can be reached. A separate durable development claim is single-use,
exactly repeatable across restart, and rejects a foreign or conflicting claim
before the scripted dispatcher is invoked. This is a test boundary and leaves
Bootstrap inert; it cannot establish real-money activation evidence.

P12-M02-T002 adds the separate protective path. Core
`ProtectiveExecutionRequest` binds a Stop, TakeProfit, or Emergency intent to a
known intent-owned position and requires reduce-only, opposite direction,
matching instrument, and quantity no greater than the position quantity. The
App coordinator obtains a durable protective claim without running entry risk or
acquiring entry capacity. The claim checks the current acknowledged revision and
the latest durable position snapshot before any injected dispatcher call; stale,
manual, unknown, conflicting, or same-side state fails closed. Repeated claims
return the existing lifecycle without another dispatch. The Binance mapping is a
pure One-way/BOTH boundary: Stop and TakeProfit use reduce-only Algo market
types, while Emergency uses a reduce-only market order. Hedge and unknown modes
remain unsupported.

P12-M02-T003 records activation-separation evidence only after a completed
scripted development-harness profile. `ActivationSeparationEvidenceBuilder`
accepts the sealed `PrivateExecutionDevelopmentHarness`, bounded source and
profile labels, a captured UTC timestamp, and a nonzero fully passing profile.
The resulting immutable evidence has a deterministic identity and carries the
scripted dispatch count, while private transport, credentials, and real-money
activation remain fixed closed facts. This is review provenance only: no
activation command, signer, endpoint, or live dispatch is exposed, and a later
explicit user command remains required by the real-money gate.
# Local paper configuration (P16-M01-T001)

`LocalPaperConfiguration` is an immutable App contract with format version,
configuration identity, fixed `local-paper` mode, positive virtual initial
equity, explicit risk fraction, existing shared `RiskEvaluationConfiguration`,
and separately versioned `SimulatedFillAssumptions`. Unknown risk limits remain
unknown; equity, risk fraction, fees, slippage, and assumption descriptions have
no trading defaults.

`Parse`, `Load`, and `ToJson` use strict, case-sensitive JSON. Unknown members,
duplicate properties at any nesting level, missing required fields, unsupported
versions, and any mode other than `local-paper` fail closed. Core's existing
strict serializer validates nested monetary/risk contracts. Parser diagnostics
do not include source JSON or values. `Load` only reads its explicit file path;
it does not use environment overrides, create state, call a network, or compose
simulation or execution services. This configuration contract exposes no
credentials, endpoints, activation field, order, or execution authority. T001
adds configuration only; paper execution and restart/reconciliation evidence
remain T002/T003 work.

## Local paper executor (P16-M01-T002)

`LocalPaperObservation` is an immutable App boundary over one persisted
`ReplayEnvelope`. An optional `LocalPaperApplication` carries the complete Core
`RiskEvaluationInput` and an independent Backtesting `SimulatedRiskPlan`.
Observations are bounded causal events; the executor never fetches missing
market data, consults a private transport, or emits an order. The result keeps
the shared `RiskEvaluationResult` separate from the injected fill simulation
result and has explicit Recorded, Applied, AlreadyProcessed, and Blocked
statuses.

`LocalPaperExecutor.Process` canonicalizes configuration, risk input, plan,
fill assumptions, model version, and every causal timestamp (event, exchange
when available, receive, availability, and stable sequence) into content
identities. It rejects mismatched decision/risk/plan links, future or
unavailable risk evidence, missing risk math or limits, configuration/model
changes, and unsupported fill assumptions before any simulation side effect.
For an approved application it calls `RiskEvaluationEngine` with the shared
`IRiskMathCalculator`, then durably prepares the observation intent and virtual
portfolio reservation. Only after that transaction commits does it run the
existing `SimulatedRiskSimulator` with the injected `IFillSimulator`.

The executor accepts complete supported fills and may retain a deterministic
EndOfData position. A protective partial fill, overfill, changed fill model,
corrupt replay evidence, or failed simulation remains blocked; an
already-prepared reservation is retained so capacity cannot be silently
released. When EndOfData leaves quantity open, the next bounded observation
replays the same persisted plan over the extended causal prefix without a new
entry application, commits only new fill identities, and retains the original
reservation until a stop, target, or time exit closes the position. Completion
is one store transaction for primitive fills, reservation release/retention,
derived virtual ledger, result, and optimistic revision checkpoint. Exact
retries are idempotent; changed identities or fill evidence are refused. The
virtual ledger derives equity, realized P/L, fees, slippage, positions, and
reserved capacity from the durable journal, and reopens deterministically after
a process restart. Legacy string-only paper rows remain readable but block
executor recovery, keeping local paper separate from private execution and
real-money activation.

## Local paper evidence and recovery (P16-M01-T003)

`LocalPaperEvidence` is a read-only App report over one run-scoped
`LocalPaperStore` stream. It reads the persisted observation and primitive-fill
journal together with the ledger recomputed from those fills and reservations;
it does not simulate, submit, or authorize an order. The immutable report keeps
the source identity, configuration identity, per-observation model and plan
identities, decision/risk/plan/fill-assumption JSON, all causal event/exchange/
receive/availability timestamps and stable sequences, fills, realized P/L,
fees, slippage, reservations, blockers, and unknowns. Open virtual positions
remain explicit unknowns rather than being treated as a completed result.

The report is format-versioned and content-addressed with SHA-256 over a
canonical body. Deserialization verifies both identity and canonical bytes;
tampering, a corrupt checkpoint, an invalid envelope, or an unavailable stream
returns a blocked/unknown report without an optimistic ledger. The
`ToPromotionEvidenceSnapshot` adapter uses the existing `LocalPaper` category,
retains causal fields and synthetic provenance, and always records
`execution-authority-not-granted`, real-order exclusion, and private/network/
credential exclusions. It does not make a promotion decision.

Restart proof compares uninterrupted, repeated, and process-restarted traces.
Interruption injection is limited to the durable boundaries before intent,
after intent, before commit, and after commit before response. Pending intent
and reservation recovery remains fail-closed and deterministic; a committed
fill/ledger/checkpoint is returned idempotently after a response interruption.
Partial take-profit and EndOfData positions retain their original reservation
until a later bounded observation closes them. Configuration or fill-model
identity changes are refused before recovery simulation.
