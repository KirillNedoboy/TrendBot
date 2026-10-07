# Execution model

Execution is deferred to Phase 12 and remains disabled during bootstrap, shadow, and initial research. The future state machine must handle accepted, rejected, partially filled, cancelled, unknown, timed-out, and reconciled orders with idempotent client identities and native protective orders where supported.

Core defines passive OrderIntent and Position contracts in Phase 2. They do not submit orders or model persisted execution state. No Binance calls, credentials, order endpoints, or execution behavior are present in the current solution.
