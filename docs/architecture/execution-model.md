# Execution model

Execution is deferred to Phase 12 and remains disabled during bootstrap, shadow, and initial research. The future state machine must handle accepted, rejected, partially filled, cancelled, unknown, timed-out, and reconciled orders with idempotent client identities and native protective orders where supported.

Core defines passive OrderIntent and Position contracts in Phase 2. P03-M02-T001
adds SqliteOrderIntentJournal in Infrastructure so a complete OrderIntent can be
durably recorded and recovered before any future execution side effect. This
state only means that an intent is awaiting future execution; it does not
submit or authorize an order.

No Binance calls, credentials, order endpoints, reservations, fills,
reconciliation, or execution behavior are present in the current solution.
