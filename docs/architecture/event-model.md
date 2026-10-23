# Event model

P02-M01 defines immutable Core market-event records with caller-supplied
identity, instrument, and UTC event time. P02-M01-T002 adds local Bar, Setup,
and Position invariants plus a pure comparison diagnostic. Event identity is
the concrete event type, instrument, and event ID: equal records with that
identity are duplicates, while differing records are identity conflicts. Other
events are compared by instrument and EventTime; equal timestamps remain
unresolved, and EventId is never a tie-breaker. This diagnostic does not select
processing order.

P02-M01-T003 adds CoreJsonSerializer, the BCL-only serialization entry point
for current Core contracts. It writes compact camelCase JSON with strict
numeric tokens and string enum values, rejects unknown members and missing
required constructor parameters, and surfaces malformed JSON or domain
invariant failures as JsonException. Decimal values use the built-in exact
decimal representation, UtcTimestamp is normalized by its validating
constructor, and ImmutableValueList<T> is serialized as an ordered array
through a defensive-copy converter.

MarketEvent uses fixed type discriminators: trade, bar, bookDelta,
fundingUpdate, and oiUpdate. The serializer uses the polymorphic base contract
for runtime-inferred event values so the discriminator is present on every
event payload. The CausalMarketEvent wrapper preserves that payload contract
and serializes its causal metadata alongside the nested discriminator.

P02-M02-T001 adds the immutable CausalMarketEvent wrapper with the original
MarketEvent payload, local ReceiveTime, local AvailabilityTime, non-negative
StableSequence, and optional ExchangeTime. Payload and local timestamps are
required, availability cannot precede receive, and exchange time is retained
independently without being compared to either local timestamp. Sequence
assignment, uniqueness, and monotonicity are properties of one recorded
processing stream and belong to the outer recording component; this Core
contract does not assign or globally validate sequence values.

CausalEventCursor defines an immutable inclusive visibility boundary with AsOf
and MaxStableSequence. An event is eligible when its payload EventTime,
AvailabilityTime, and StableSequence are each less than or equal to the
corresponding cursor bound. ReceiveTime remains preserved metadata and is not
substituted for event time. CausalMarketEventOrdering.Compare orders eligible
events by payload EventTime and then StableSequence using CompareTo; zero means
only that this ordering key is equal and does not imply a duplicate payload.
Filtering happens before ordering, so unavailable future events cannot affect an
as-of view.

P03-M01-T002 adds ReplayEnvelope, an immutable Core record with format version
1, a caller-defined stream identity, and the complete CausalMarketEvent.
Unknown envelope versions fail during construction or strict JSON
deserialization. Infrastructure persists the envelope version beside the
existing causal tuple and migrates version-one journal rows to format version 1
transactionally.

ReplaySnapshotBuilder applies the cursor's inclusive event-time,
availability-time, and stable-sequence bounds before ordering eligible
envelopes by payload event time and stable sequence. The resulting stream
snapshot is immutable; this is a deterministic view boundary and does not
introduce a full replay runner or recovery policy.

Core event records contain no Binance wire DTOs. Late, missing, and gap
recovery, quality flags, and venue-specific ordering guarantees remain
deferred to their later tasks.

P09-M01-T001 adds `ReplayRunner` in `TradingBot.Backtesting` as the historical
delivery boundary. It validates one stream, removes exact duplicate envelopes
with a diagnostic, and stops before delivery when an event identity or stable
sequence conflicts. Delivery order uses a monotonic virtual availability clock
of `max(EventTime, AvailabilityTime, Bar.CloseTime)`, then event time and the
persisted stable sequence. Each handler receives an immutable
`ReplayStreamSnapshot` built with the existing inclusive `CausalEventCursor`,
so unavailable or future events cannot enter a causal prefix. Receive time and
optional exchange time remain metadata on every delivered envelope. A handler
exception leaves that step uncommitted and prevents later deliveries; the
runner returns diagnostics and no execution or authorization state.
