# Execution model

Execution is deferred to Phase 12 and remains disabled during bootstrap, shadow, and initial research. The future state machine must handle accepted, rejected, partially filled, cancelled, unknown, timed-out, and reconciled orders with idempotent client identities and native protective orders where supported.

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
