# SQLite event journal and replay envelopes

P03-M01-T001 adds the first persistence boundary in TradingBot.Infrastructure.
P03-M01-T002 keeps that boundary and adds the versioned ReplayEnvelope Core
contract. P03-M02-T001 adds a separate durable order-intent journal that stores
an immutable intent before any future execution side effect. P03-M02-T002
extracts the shared SQLite initializer and adds passive Core portfolio limits
and reservations with a durable atomic reservation store. P03-M02-T003 adds
durable operational fills, immutable position snapshots, and reconciliation
checkpoints. SqliteEventJournal
records artificial causal events in a file-backed SQLite database so a process
restart does not remove the event payload, its causal metadata, or the envelope
format version. TradingBot.Core remains BCL-only; the runtime persistence
package is Microsoft.Data.Sqlite 10.0.12 in Infrastructure.

## API and migration

The synchronous API is intentionally small:

- SqliteEventJournal.Open(databasePath, commandTimeoutSeconds = 30) opens a
  file-backed journal and applies the supported migration.
- Append(streamId, causalEvent) records one caller-assigned stable sequence and
  returns false for an identical existing row.
- Append(replayEnvelope) records the envelope's stream, format version, payload,
  and causal tuple using the same idempotent sequence rules.
- ReadStream(streamId) returns an immutable snapshot ordered by stable sequence.
- ReadEnvelopes(streamId) returns immutable versioned envelopes ordered by stable
  sequence. ReadEnvelopeStream is an equivalent named read entry point.
- SqliteOrderIntentJournal.Open(databasePath, commandTimeoutSeconds = 30) opens
  the shared file-backed schema and applies the supported migration.
- SqliteOrderIntentJournal.Append(orderIntent) stores canonical Core JSON and
  returns false for an identical existing intent. Conflicting IntentId or
  ClientOrderId values fail without changing the journal.
- SqliteOrderIntentJournal.Find(intentId) reconstructs the durable intent or
  returns null when it is absent.
- SqlitePortfolioReservationStore.Open(databasePath, commandTimeoutSeconds = 30)
  opens the same file-backed schema and applies the shared migration.
- Register(limits) persists explicit immutable portfolio limits. An identical
  retry returns false, while a conflicting portfolio identity throws.
- FindLimits(portfolioId), ReadReservations(portfolioId), and
  FindReservation(reservationId) read the persisted immutable state.
- TryReserve(intent, reservation) persists the existing OrderIntent and its
  reservation together in one immediate serializable transaction. It returns
  Reserved, AlreadyReserved, or InsufficientCapacity.
- RecordObservation(fill, positionSnapshots, checkpoint) persists the supplied
  fill, immutable position snapshots, and the next portfolio checkpoint in one
  immediate serializable transaction. It returns Recorded or AlreadyRecorded.
  A checkpoint-only observation is valid for a reconciliation update.
- ReadCheckpoint(portfolioId), FindFill(fillId), and
  ReadPositionSnapshots(portfolioId) reconstruct the persisted operational
  state after restart.
- AcknowledgeReconciliation(portfolioId, revision) persists an explicit
  acknowledgement. CanRetry(portfolioId) requires the current revision to be
  acknowledged with no unknown or manual markers.
- ReleaseAfterReconciliation(portfolioId, intentId, revision) marks the full
  reservation released only after the acknowledged current checkpoint shows
  no open intent and a closed associated position. Repeating the release is
  idempotent.

Each operation opens and closes its own connection. Connection strings disable
pooling and use a positive finite command timeout. SQL values are parameters;
schema-version PRAGMAs contain only the internal constant version. The event,
intent, and reservation stores use the shared initializer, WAL mode, and
synchronous=FULL.

Schema version 0 means an empty SQLite file. The transactional 0 -> 1
migration creates the original event_journal; the transactional 1 -> 2
migration adds envelope_version with value 1 for every existing row; and the
transactional 2 -> 3 migration adds order_intents. The transactional 3 -> 4
migration adds portfolios and reservations, and the transactional 4 -> 5
migration adds the reservation release marker, fills, position snapshots, and
reconciliation checkpoints without rewriting event_journal or order_intents.
user_version is the database migration version, not a Core contract version or
a wire-schema version. A non-empty version-0 file and an unknown version are
rejected without replacing existing tables. Version 5 is validated before use,
including BINARY collation on event, intent, portfolio, reservation, fill, and
snapshot identities. The 2 -> 3, 3 -> 4, and 4 -> 5 migrations preserve
existing event and intent rows.

The version-2 event table, retained in schema version 5, is:

| Column | Meaning |
|---|---|
| stream_id | Caller-defined stream identity, compared with BINARY collation |
| stable_sequence | Non-negative caller-assigned sequence, part of the primary key |
| payload_json | Canonical Core JSON for the MarketEvent payload |
| event_time_ticks | Payload event time as UTC DateTime ticks |
| receive_time_ticks | Local receive time as UTC ticks |
| availability_time_ticks | Local availability time as UTC ticks |
| exchange_time_ticks | Optional independent exchange time as UTC ticks |
| envelope_version | Supported ReplayEnvelope format version |

The primary key is (stream_id, stable_sequence) and the table is WITHOUT
ROWID. Decimal market values remain in the canonical JSON text; no decimal is
converted to a SQLite REAL column.

The version-3 order-intent table is:

| Column | Meaning |
|---|---|
| intent_id | Core OrderIntent identity, BINARY primary key |
| client_order_id | Client identity, BINARY unique key |
| intent_json | Canonical Core JSON for the complete immutable OrderIntent |

The table is WITHOUT ROWID. An identical retry commits and returns false. A
different value for either identity throws before insert and leaves both the
event journal and intent rows unchanged. A successful append returns only after
its transaction commits.

The version-4 portfolio tables, retained in schema version 5, are:

| Table | Stored state |
|---|---|
| portfolios | Portfolio identity in a BINARY primary key and canonical JSON for explicit `PortfolioLimits` |
| reservations | Reservation identity and portfolio/intent identities in BINARY columns, plus canonical JSON for `PortfolioReservation`; `intent_id` is unique |

Both tables are WITHOUT ROWID. Monetary values and decimal precision remain in
canonical JSON text; SQLite never stores them as REAL. `TryReserve` reads the
registered limits and existing reservations inside one immediate serializable
transaction, adds decimal totals with checked application arithmetic, and
persists the OrderIntent and reservation together only when every explicit
risk, notional, and position-slot limit remains available. Identical retries
return `AlreadyReserved`; capacity overflow returns `InsufficientCapacity`
without inserting an intent or reservation. Identity conflicts, missing
portfolios, currency mismatches, write-lock failures, and invalid persisted
state fail without partial mutation.

The version-5 operational tables are:

| Table | Stored state |
|---|---|
| fills | Fill identity and intent identity in BINARY columns, its observation revision, and canonical Core `Fill` JSON |
| position_snapshots | Portfolio/position/revision identities in BINARY or INTEGER columns and canonical Core `PortfolioPositionSnapshot` JSON |
| reconciliation_checkpoints | One current checkpoint per portfolio with its monotonic revision and canonical Core `ReconciliationCheckpoint` JSON |

Operational writes require the next revision and use checked decimal
arithmetic in application code when validating aggregate fill quantities
against the persisted intent quantity. Identical observations are idempotent;
identity conflicts, aggregate overflow, stale revisions, and revision gaps fail
before commit. A new observation always stores `Unreconciled`, including when
the caller supplied an acknowledged value. Fill and snapshot rows are
append-only observations, while the current checkpoint row records the latest
state. Unknown and manual markers remain explicit in the checkpoint.
Acknowledgement is persisted separately and does not clear those markers, so
the retry guard remains closed until they are resolved. A complete reservation
stays counted through partial fills. Release marks that complete reservation
only after an acknowledged current checkpoint confirms the order is closed and
every latest associated position snapshot is closed; repeated release leaves
the durable state unchanged.

## Append and read guarantees

The caller assigns stable sequences. A new row must be greater than the current
maximum sequence in its stream, while gaps are allowed. The key comparison and
insert run in one immediate write transaction. An identical retry returns
false; a different value at the same key or any lower new sequence throws
InvalidOperationException and leaves the database unchanged. Equal market-event
identity at a later sequence remains a separate recorded row. Streams are
independent and late event time does not reorder the stored sequence.

Reads reconstruct ReplayEnvelope from the canonical payload, the envelope
version, and the causal columns. They verify the event, receive, availability,
and optional exchange ticks against the reconstructed values. Unknown envelope
versions, invalid JSON, invalid ticks, and inconsistent columns surface as
InvalidDataException; SQLite provider errors remain provider errors. The
journal does not apply cursor filtering or replay ordering beyond returning the
stream's stable-sequence order.

ReplaySnapshotBuilder in TradingBot.Backtesting builds one immutable
ReplayStreamSnapshot. It applies CausalEventCursor.IsEligible to each envelope
first, then orders eligible events by payload EventTime and StableSequence.
Receive time is retained metadata and is never substituted for event time.

## Evidence and limits

tests/TradingBot.Infrastructure.Tests/SqliteEventJournalTests.cs,
ReplayEnvelopeJournalTests.cs, and SqliteOrderIntentJournalTests.cs cover
restart persistence across all five current MarketEvent kinds, exact decimal
and UTC tick preservation, nullable exchange time, independent BINARY streams,
late event time, idempotent retries, conflicts, immutable snapshots, migration
settings, version-one and version-two migrations, unsupported databases,
corrupted rows, and held write locks with a one-second timeout. Intent tests
cover every OrderIntent field, canonical JSON, restart, both identity conflicts,
rollback, and shared event/intent use of one schema. Replay tests cover strict
 envelope serialization and causal filtering/order. SqlitePortfolioReservationStoreTests
 cover explicit limit registration, exact risk/notional/slot
boundaries, independent-connection restart recovery, idempotent retries,
identity and currency conflicts, rollback under a held writer, three barrier
races, and 3 -> 4 migration preservation of events and intents. The
operational state tests cover causal fill and position persistence across
restart, duplicate and conflicting observations, aggregate fill overflow,
stale revisions, acknowledgement invalidation, unknown/manual retry guards,
partial-fill reservation retention, idempotent release, and 4 -> 5 migration
preservation of events, intents, portfolios, and reservations.
ArchitectureTests confirms Infrastructure still references Core without
referencing Exchange, Backtesting, or App.

The journal, snapshot builder, reservation store, and operational state store
remain bounded recording and view boundaries. They do not provide a full replay
runner, StrategyRun or ConfigVersion persistence, Parquet storage, venue
adapters, execution, or recovery policy. Those guarantees belong to later
Phase 3 and Phase 12 tasks.
