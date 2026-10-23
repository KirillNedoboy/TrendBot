# Decision projections

`DecisionProjectionSnapshot` is the version-one Core projection for one Decision Engine result. It retains the complete `DecisionEngineResult`, the exact `DecisionEngineConfiguration`, the `SetupLifecycleConfiguration`, and the lifecycle snapshot used as the recovery checkpoint. The checkpoint keeps setup identity, causal cursor, local availability time, processed event receipts, latest evidence, diagnostics, and `RecoveryRequired` state.

The projection records `decision-reasons-v1` as an explicit reason-catalog version. Its lower-case SHA-256 identity is computed from canonical JSON with the identity property ignored. Construction and read-back re-evaluate the saved input with the saved Decision Engine configuration and reject semantic drift, unsupported format metadata, non-canonical JSON, or identity mismatch.

`SqliteDecisionProjectionStore` stores the projection in the BLOB-keyed `decision_projections` table. Exact retries are idempotent; a different payload at the same identity fails closed. SQLite schema version 10 adds the table transactionally from schema 9 and leaves existing journal, operational, capture, screener, universe-selection, and feature-quality rows intact.
