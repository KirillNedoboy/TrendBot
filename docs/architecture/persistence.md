# SQLite event journal

P03-M01-T001 adds the first persistence boundary in `TradingBot.Infrastructure`.
`SqliteEventJournal` records artificial `CausalMarketEvent` values in a file-backed
SQLite database so a process restart does not remove the event payload or its
causal metadata. `TradingBot.Core` remains BCL-only; the only runtime package
added by this task is `Microsoft.Data.Sqlite` 10.0.12 in Infrastructure.

## API and migration

The synchronous API is intentionally small:

- `SqliteEventJournal.Open(databasePath, commandTimeoutSeconds = 30)` opens a
  file-backed journal and applies the supported migration.
- `Append(streamId, causalEvent)` records one caller-assigned stable sequence and
  returns `false` for an identical existing row.
- `ReadStream(streamId)` returns an immutable snapshot ordered by stable sequence.

Each operation opens and closes its own connection. Connection strings disable
pooling and use a positive finite command timeout. SQL values are parameters;
schema-version PRAGMAs contain only the internal constant version. Supported
files use WAL mode and `synchronous=FULL`.

Schema version `0` means an empty SQLite file. The transactional `0 -> 1`
migration creates `event_journal` and sets `PRAGMA user_version` to `1`.
`user_version` is the database migration version, not a Core contract version or
a wire-schema version. A non-empty version-0 file and an unknown version are
rejected without replacing existing tables. Version 1 is validated before use,
including the BINARY collation on the stream key.

The version-1 table is:

| Column | Meaning |
|---|---|
| `stream_id` | Caller-defined stream identity, compared with BINARY collation |
| `stable_sequence` | Non-negative caller-assigned sequence, part of the primary key |
| `payload_json` | Canonical Core JSON for the `MarketEvent` payload |
| `event_time_ticks` | Payload event time as UTC `DateTime` ticks |
| `receive_time_ticks` | Local receive time as UTC ticks |
| `availability_time_ticks` | Local availability time as UTC ticks |
| `exchange_time_ticks` | Optional independent exchange time as UTC ticks |

The primary key is `(stream_id, stable_sequence)` and the table is `WITHOUT
ROWID`. Decimal market values remain in the canonical JSON text; no decimal is
converted to a SQLite `REAL` column.

## Append and read guarantees

The caller assigns stable sequences. A new row must be greater than the current
maximum sequence in its stream, while gaps are allowed. The key comparison and
insert run in one immediate write transaction. An identical retry returns
`false`; a different value at the same key or any lower new sequence throws
`InvalidOperationException` and leaves the database unchanged. Equal market-event
identity at a later sequence remains a separate recorded row. Streams are
independent and late event time does not reorder the stored sequence.

Reads reconstruct `CausalMarketEvent` from the canonical payload and the causal
columns. They verify the event, receive, availability, and optional exchange
ticks against the reconstructed values. Invalid JSON, invalid ticks, and
inconsistent columns surface as `InvalidDataException`; SQLite provider errors
remain provider errors. The journal does not apply cursor filtering or replay
ordering beyond returning the stream's stable-sequence order.

## Evidence and limits

`tests/TradingBot.Infrastructure.Tests/SqliteEventJournalTests.cs` covers
restart persistence across all five current `MarketEvent` kinds, exact decimal
and UTC tick preservation, nullable exchange time, independent BINARY streams,
late event time, idempotent retries, conflicts, immutable snapshots, migration
settings, unsupported databases, corrupted rows, and a held write lock with a
one-second timeout. `ArchitectureTests` confirms Infrastructure still references
Core without referencing Exchange, Backtesting, or App.

The journal is a recording boundary only. It does not provide replay envelopes,
causal filtering, a replay runner, StrategyRun or ConfigVersion persistence,
Parquet storage, order intents, reservations, execution, or recovery policy.
Those guarantees belong to later Phase 3 tasks.
