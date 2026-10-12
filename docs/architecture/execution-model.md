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
registers immutable limits and atomically persists an existing OrderIntent with
its reservation in one immediate serializable transaction. It returns
`Reserved`, `AlreadyReserved`, or `InsufficientCapacity` and reads totals with
checked application decimal arithmetic. This is a durable capacity boundary;
it does not submit or authorize an order, and no release is provided before
the later reconciliation work.

P03-M02-T003 adds passive operational `Fill`, `PortfolioPositionSnapshot`, and
`ReconciliationCheckpoint` contracts. A fill carries intent/instrument
identity, positive quantity and price, optional fee, event/receive/
availability/exchange timestamps, and stable sequence. Position snapshots
retain Core `Position` values and distinguish known intent-owned, manual, and
unknown state. `SqlitePortfolioReservationStore.RecordObservation` writes
observations and the monotonic portfolio revision together. Identical repeats
are idempotent; conflicts, aggregate fill overflow, stale revisions, and gaps
fail atomically. New observations clear acknowledgement. Retry remains closed
until the current revision is acknowledged with no unresolved unknown/manual
markers. Partial fills retain the complete reservation, and
`ReleaseAfterReconciliation` releases it only after a current acknowledged
checkpoint confirms the order and associated position are closed.

No Binance calls, credentials, order endpoints, venue adapters, or execution
behavior are present in the current solution.
