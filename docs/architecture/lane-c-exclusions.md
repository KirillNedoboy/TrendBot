# Lane C capture exclusions

`LaneCCaptureExclusionLedger` is a versioned Infrastructure record tied to the
quality report identity, manifest id, manifest checkpoint, and unchanged
coverage value. Entries use safe references to manifest segments, optional
instrument/source scope, known sequence and event-time bounds, stable reason
codes, and guarded provenance. Unknown bounds remain null rather than being
estimated.

The builder sorts entries by their complete evidence, removes exact duplicate
entries, and validates segment and sequence references against the supplied
manifest. Readback repeats these reference and coverage checks and validates
the linked report identity and checkpoint. The ledger is written as canonical
JSON through a flushed temporary file followed by atomic replacement.

Retention is represented only by the existing `CaptureRetentionPlanner.DryRun`
result. Its original nullable policy, as-of time, candidates, and reasons are
retained; candidates do not imply deletion. The ledger does not alter manifest
coverage, remove segments, or change capture checkpoints. No retention policy
defaults are introduced.
