# ADR-004 — Persistence, intents, and research data

## Context

Operational state, journals, configuration versions, order intents, reconciliation checkpoints, and high-frequency research datasets have different access and durability needs. A timeout can leave a side effect unknown, while a restart must not lose a reservation or replay-ordering field.

## Decision

Use SQLite for transactional operational state, event journals, durable order intents, reservations, fills, positions, reconciliation checkpoints, and configuration versions. Use Parquet for append-oriented high-frequency/research data. Persist event time, receive/availability time, stable processing sequence, provenance, and quality flags in replay envelopes and dataset manifests.

Write the intent and reservation transaction before an exchange side effect. Recovery reads durable state first, reconciles exchange state, and only then permits a retry or release. Atomic portfolio reservations protect concurrent decisions and partial fills.

## Alternatives considered

Use one relational store for everything, a cloud warehouse, an external streaming platform, or client-only in-memory intent tracking. Those choices either increase the VPS footprint or cannot recover safely after a process failure.

## Consequences

The system needs explicit migrations, busy/restart handling, idempotent identities, reservation invariants, dataset manifests, and provenance. Unknown/manual positions remain explicit reconciliation blocks. The split keeps the VPS footprint small while preserving efficient research scans.
