# Lane C quality report

`LaneCQualityEvaluator` is a pure BCL-only Core assessment tied to one
instrument, `LaneCFeatureInput`, configuration, feature result, and
`CausalEventCursor`. It
recomputes the feature result identity/content before assessing it, then
filters records by event time, availability time, and stable sequence. Required
provider inputs and optional maximum silence are caller supplied. An unknown
required freshness policy or an absent required provider produces
`InsufficientData`; a stale provider, active depth gap, or identity conflict
produces an explicit quality failure. Historical gaps, duplicates, conflicts,
missing fields, clock skew, and stale signals remain counted even when they do
not invalidate the current recovered state.

Known diagnostic codes are normalized into stable categories and counted at
most once per capture row. Unknown codes retain only a safe code token, source,
stream, stable sequence, event time, and availability time. Raw messages,
payloads, and secret-shaped values are not included in Lane C quality evidence.
The evidence identity hashes its version, input identity, cursor, status,
signal counts, reasons, and unknown diagnostic provenance.

`DecisionEvidence.FromLaneCQuality` preserves the quality identity, input,
version, cursor, status, and reasons. Ready quality is Neutral evidence;
unavailable quality maps to the existing Unknown status; stale, gapped, and
conflicting quality maps to Veto. The existing Decision Engine blocks Unknown
when Lane C is required.

`LaneCQualityReportBuilder` reads bounded durable segments and requires the
store checkpoint and every feature-input row to match the supplied manifest.
The report includes causal quality evidence and separate manifest-wide
`CaptureQualitySnapshot` counters. The report store writes strict canonical JSON
through a flushed temporary file and atomic rename; readback verifies the
manifest/checkpoint and recomputed report identity. `LaneCQualityReportService`
in App composes feature evaluation with report construction.

`CaptureQualitySnapshot` adds an optional `conflictCount` field; older JSON
without it reads as zero. The generic snapshot keeps historical record counts,
while its as-of freshness point uses only receives at or before the explicit
UTC `asOf`. No persistence schema, package, project reference, trading
threshold, or execution behavior is added.
