# ADR-009 — Execution idempotency and durable reconciliation

## Context

HTTP timeouts and unknown exchange execution status can leave an order accepted even when the client did not receive a response. Retries can duplicate orders, and a restart can otherwise lose a portfolio reservation or partial-fill protection.

## Decision

Every order intent receives a deterministic client identity and a durable execution record before any side effect. Portfolio capacity is reserved atomically with the intent. Unknown outcomes are queried and reconciled before retry; private streams and periodic snapshots converge open orders, fills, positions, reservations, and protective actions. Partial fills use explicit reduce-only protection. Unknown or manually created positions are visible fail-closed states until reconciled.

## Alternatives considered

Blind retries, client-only in-memory deduplication, or relying on exchange defaults. These choices cannot guarantee safe recovery after a timeout, restart, partial fill, or manual intervention.

## Consequences

Execution requires durable state, private stream reconciliation, query-after-timeout behavior, atomic reservation tests, and failure cases for accepted, rejected, partial, canceled, timeout, unknown, and manual-position outcomes. Protective actions take precedence during recovery.
