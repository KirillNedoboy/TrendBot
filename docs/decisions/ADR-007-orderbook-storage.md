# ADR-007 — Orderbook storage and recovery

## Context

Lane C needs L2 history, and Binance diff-depth streams require a snapshot plus sequence-aware deltas. Gaps and reconnects are normal failure cases.

## Decision

Keep a recoverable local orderbook state in the market-data adapter, persist normalized depth/trade events in the research store, and rebuild after sequence gaps or stale streams.

## Alternatives considered

Persist only periodic snapshots, trust deltas without recovery, or outsource the book to an external service.

## Consequences

Storage volume and retention need explicit configuration. Sequence provenance and gap tests are required before Lane C is allowed to influence decisions.
