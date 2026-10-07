# Event model

P02-M01 defines immutable Core market-event records with caller-supplied identity, instrument, and UTC event time. P02-M01-T002 adds local Bar, Setup, and Position invariants plus a pure comparison diagnostic. Event identity is the concrete event type, instrument, and event ID: equal records with that identity are duplicates, while differing records are identity conflicts. Other events are compared by instrument and EventTime; equal timestamps remain unresolved, and EventId is never a tie-breaker. This diagnostic does not select processing order.

P02-M01-T003 adds `CoreJsonSerializer`, the BCL-only serialization entry point for current Core contracts. It writes compact camelCase JSON with strict numeric tokens and string enum values, rejects unknown members and missing required constructor parameters, and surfaces malformed JSON or domain invariant failures as `JsonException`. Decimal values use the built-in exact decimal representation, `UtcTimestamp` is normalized by its validating constructor, and `ImmutableValueList<T>` is serialized as an ordered array through a defensive-copy converter.

`MarketEvent` uses the fixed `$type` discriminators `trade`, `bar`, `bookDelta`, `fundingUpdate`, and `oiUpdate`. The serializer uses the polymorphic base contract for runtime-inferred event values so the discriminator is present on every event payload. No exchange, receive/availability, or stable processing sequence fields are introduced by this task.

P02-M02-T001 adds exchange time, receive/availability time, and stable processing sequence; replay ordering and late, missing, and gap handling remain later work.

Core event records contain no Binance wire DTOs. Persistence and replay envelopes are reserved for Phase 3, and unknown exchange fields and ordering guarantees remain deferred to the relevant Binance research task.
