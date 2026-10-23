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

No Binance calls, credentials, order endpoints, venue adapters, or execution
behavior are present in the current solution.
