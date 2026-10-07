# ADR-003 — Event and market-data transport

## Context

Live, replay, and backtest need one ordering model while WebSocket streams can duplicate, arrive late, or contain sequence gaps. Event time and receive/availability time answer different questions, and equal timestamps are common across feeds.

## Decision

Normalize inputs into immutable, timestamped Core events with explicit identity, source sequence, event time, exchange time when supplied, receive/availability time, and a stable processing sequence. Persist the causal ordering tuple and use it to order replay. A bounded asynchronous channel may be used at runtime boundaries, but transport details do not enter Core contracts.

Duplicate, late, gap, and recovery decisions must be explicit and auditable. Receive time must never silently replace event time, and stable processing order must resolve equal-time events deterministically.

## Alternatives considered

Direct strategy callbacks, an external message broker, or letting each lane parse exchange payloads independently. Ordering by receive time alone was rejected because it changes causal replay and can introduce lookahead.

## Consequences

Replay can reproduce event streams and tests can inject duplicates/gaps without changing the causal model. Kafka is outside V1. Every adapter and dataset manifest must preserve the four timing/order fields and quality flags.
