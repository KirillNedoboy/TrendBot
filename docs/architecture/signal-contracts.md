# Versioned public signal contracts

`TradingBot.Core.SignalContracts.cs` defines immutable BCL-only contracts. They do not evaluate setups, read market data or secrets, publish messages, persist data, or authorize orders. Every persisted record uses `CoreJsonSerializer`; unsupported format versions, malformed hashes, incompatible links, and unknown JSON members fail closed.

## Separate records

| Contract | Purpose | Identity or hash |
|---|---|---|
| `SignalIdentity` | Logical occurrence: instrument, venue, 15m setup timeframe, direction, family, profile/family versions, and setup event ID | `SignalId`, SHA-256 of the logical identity fields; excludes evidence and card content |
| `SignalEvidenceRecord` | Completeness state, quality reasons, public input provenance, and profile-configuration hash | `RecordHash`, SHA-256 of evidence content; linked by `SignalId` |
| `SignalRecord` | Immutable passive card: assessment state, setup interval, decision time, reference price, evidence hash, optional calibrated probability | `RecordHash`, SHA-256 of card content; distinct from `SignalId` and evidence hash |
| `SignalDeliveryReceipt` | One output attempt/acknowledgment/rejection/unknown receipt, bound to one exact signal record | `ReceiptId`, content identity; links `SignalId` and `SignalRecordHash` |
| `SignalOutcomeRecord` | Independent measured/expired/unknown outcome and method version | `OutcomeId`, content identity; links `SignalId`, `SignalRecordHash`, and `EvidenceRecordHash` |

Signal identity intentionally excludes payload hashes and mutable card/evidence content. A corrected or changed evidence record can therefore preserve `SignalId` while changing evidence and signal `RecordHash`. Record hashes, receipt IDs, and outcome IDs are separate values with separate meanings. Each hash/ID uses lowercase SHA-256 over the `CoreJsonSerializer` encoding of its declared content material, excluding that material's own hash/ID.

## Causal provenance

Each `SignalInputReference` carries public source ID/version, timeframe role, event ID, payload SHA-256, event time, availability time, and stable sequence. Setup inputs use native `15m`; higher-timeframe inputs use native `1h`. Provenance requires both roles, a profile version/configuration hash, a decision cut, and an HTF cut. The HTF cut cannot exceed the decision cut in event time or stable sequence. Each input's event time, availability time, and stable sequence must be visible at its role's cut.

A signal's setup interval must close no later than its decision cut or `decisionAt`; `decisionAt` cannot precede the decision cut. Its reference price must name an input retained in the evidence provenance at that input's event time. `SignalRecord.ValidateEvidence`, `SignalDeliveryReceipt.ValidateFor`, and `SignalOutcomeRecord.ValidateFor` verify links when separately loaded records are joined.

## Fail-closed state and authority

`SignalEvidenceState.Unknown` requires at least one reason. `ConfirmedOnClosedBar` can only be created with `Complete` evidence; it records closed-bar confirmation only, not statistical validation, permission, or order authorization. `Research` may retain unknown evidence. The contract contains no `Validated`, `Allow`, `OrderAuthorized`, quantity, leverage, portfolio, position, or execution fields.

Probability and calibration version are both absent/unknown or both explicitly present; probability must be within `[0, 1]`. No probability or other trading parameter is supplied by default. Unknown delivery/outcome results require reasons. An acknowledgment is a receipt, not an exactly-once guarantee. An outcome is observation evidence, never a fill or trade result.

## Wire compatibility

Each persisted top-level contract has an explicit format version; nested value contracts inherit their containing schema. Constructors validate required fields, enum values, lower-case SHA-256 forms, local content hashes, cut eligibility, and temporal ordering. Factories and `ValidateFor` methods verify cross-record links when related records are joined. `CoreJsonSerializer` rejects unknown properties, including execution/activation fields. The contracts have no file, network, database, credential, or trading side effects.

## Local journal rendering

`signal-only render --journal <file> --symbol <symbol> --format <json|console>` reads and validates one committed stream through a read-only SQLite connection. Cards retain the stored signal/evidence hashes, 15m interval, decision and 1h cuts, input provenance, scenario anchor/alert links, current episode state, and quality reasons. The reference is marked `scenario-reference-only`; the card says `ConfirmedOnClosedBar` separately from `RESEARCH` and statistical validation `UNKNOWN`. JSON includes no channel receipt or `publishedAt` unless a receipt is actually present; the current journal has no persisted delivery-receipt rows, so both are null. Diagnostics are separate records without a direction or signal-ready state. Rendering does not claim delivery, validation, fills, or execution authority.
