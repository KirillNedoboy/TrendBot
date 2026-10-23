# Frozen research inputs

P09-M03-T001 introduces a BCL-only `FrozenResearchManifest` at the Backtesting
boundary. The manifest is created before an out-of-sample run and retains the
dataset identifier/version, bounded provenance, composition and exclusions,
the complete stream-scoped causal event set, configuration and selection
snapshots, risk/fill policy identifiers, and explicit assumptions. It contains
no result, promotion decision, or inferred threshold.

Event rows retain event time, receive time, availability time, optional exchange
time, stable sequence, stream, identity, and canonical Core payload JSON.
Canonical ordering uses stable sequence and then payload JSON/event identity so
permuted source collections produce the same event hash and manifest identity;
exact duplicate rows remain present in the frozen set. Configuration rows are
canonically ordered and hashed separately. Deserialization rejects unsupported
formats, unknown fields, non-canonical JSON, changed hashes, and a changed
identity.

`FrozenResearchManifest.RunReplay` validates the candidate stream and event
content hash before invoking the existing causal replay runner. A
`FrozenResearchManifestJournal` provides a write-once BCL record. A completed
`FrozenResearchEvaluationSnapshot` stores the manifest identity, report
identity, and the report's canonical JSON at capture time, so later caller
identity, validates that the report carries the same causal event identities
and payloads, and captures the report's canonical JSON at creation time, so
later caller changes to a report object cannot alter the captured evidence. These API
guarantees do not attest to external file immutability or real-world dataset
chronology; the fixtures in this milestone are synthetic.

`PredeclaredPromotionCriteria` is a second immutable record bound to the
manifest identity. It contains stable criterion IDs, questions, required
evidence descriptions, and explicitly unresolved blockers. Criterion and
evidence order is canonical; its SHA-256 identity includes the manifest
identity, so the same questions cannot be attached to another frozen input.
`PredeclaredPromotionCriteriaJournal` is write-once. `FrozenResearchEvaluationContext`
requires the matching criteria record and an empty unresolved-blocker list
before replay or evaluation snapshot capture; replacing a registered record or
using a foreign manifest is rejected. The API records readiness and evidence
links only; it does not compute a promotion decision or introduce a numeric
threshold.
