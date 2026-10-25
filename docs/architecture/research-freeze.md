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

P14-M02-T001 adds `FrozenOosEvaluationDefinition` and
`FrozenOosEvaluator` in Backtesting. The definition is created before replay
and binds the existing research and criteria identities to a multi-source
manifest, a SHA-256 of each typed replay payload plus its full causal ordering
entry, the frozen configuration hash, and a canonical set of model-version
declarations. It leaves all existing manifest formats and IDs unchanged. A
null model version remains null and blocks a resolved evaluation. A changed
research, criteria, multi-source manifest, replay payload, configuration, or
model version marks the prior evaluation `Invalidated` before the projection
callback runs; the report continues to identify the original frozen inputs.

The canonical `FrozenOosEvaluationReport` records the original identities,
per-source coverage/quality references and reasons, the supplied reconciliation
checkpoint, original exclusions, replay diagnostics, predeclared question
assessments and evidence links, blockers, and one causal trace entry per
delivered event. Exact duplicate inputs remain recorded as runner diagnostics,
even when established replay behavior delivers them only once. Each trace entry
preserves event, exchange, receive, and availability times and the source
sequence; an absent projection remains null. Missing quality evidence, incomplete/unknown quality,
unacknowledged or absent reconciliation, unknown required criteria, and
unresolved evidence remain explicit blockers. `Evaluated` means the evidence
assessment completed; it does not mean promoted or authorize execution. The
fixtures are synthetic and no real frozen OOS dataset is present, so they do
not establish strategy performance.
