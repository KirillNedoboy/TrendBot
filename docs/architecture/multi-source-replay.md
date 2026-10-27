# Multi-source historical replay

`MultiSourceDatasetManifest` is the immutable Core index for one aligned
research dataset. Format one records each source ID and lane kind, its source
manifest and optional quality evidence identities, bounded coverage and
quality states, observed event/availability ranges, and source sequence bounds.
Unknown coverage remains `Unknown`; the capture manifest's bounded storage is
not evidence of complete exchange history.

`MultiSourceReplayOrderEntry` retains source-local sequence, an explicitly
recorded dataset-wide stable sequence, event time, optional exchange time,
receive time, and availability time. The manifest requires one entry per
record, unique source/event and sequence identities, valid source references,
and increasing dataset-wide sequence within each source. It does not derive
cross-source chronology from input enumeration. Canonical JSON excluding the
identity field is SHA-256 hashed; constructor validation recomputes the hash.

`MultiSourceDatasetManifestStore` writes canonical Core JSON through a flushed
temporary file and atomic rename. Reopening validates the contract and its
content identity, so a modified source/order reference is rejected. Replay
delivery uses `MultiSourceReplayRunner` in Backtesting. Each typed record must
match its saved source/event/sequence/timestamp reference. The runner orders by
causal availability, event time, and saved global sequence; bar records become
available no earlier than close time. Each handler receives one cursor and an
immutable prefix filtered by event time, availability, and global sequence.
Capture diagnostics and standalone Lane B liquidation observations keep their
own payload contracts. Missing rows and conflicting identities fail the run;
exact duplicate rows are delivered once and remain visible as diagnostics.
Existing single-stream replay overloads are unchanged.

`MultiSourceReplayResult.ReadinessReasons` preserves deterministic source-quality
and coverage blockers for downstream decisions. `DecisionReadinessConfirmed` is
true only when replay succeeds and every included source reports complete
coverage with ready quality. A replay may still deliver degraded research data;
the readiness reasons must be carried into evidence and block unsupported
decision readiness. Missing or unknown coverage is never inferred as complete.

The fixed integration regression persists and reopens capture rows and the
multi-source manifest, then repeats capture/funding replay and Lane A/B/C,
lifecycle, decision, passive risk, and research simulation evaluation. It
compares canonical traces across identical runs and confirms a funding-input
change alters Lane B identity and Decision outcome. Partial/degraded capture
quality blocks the decision while retaining the original quality reasons. The
fixture's numeric values and explicit rules are synthetic test data only.

`FrozenOosEvaluationDefinition` snapshots the exact typed source payloads
before an OOS replay and verifies each row against its saved causal ordering
entry. Its content identity includes event time, exchange time, receive time,
availability time, and both source-local and dataset-wide stable sequences.
`FrozenOosEvaluator` reuses `MultiSourceReplayRunner` only when the research,
criteria, and multi-source manifest identities, configuration, payloads, and
model versions still match their frozen values. A changed value invalidates
the previous evaluation before its trace callback runs. Reports retain the
source quality evidence references and states, readiness reasons, and each
delivered trace entry's causal fields. Exact duplicate-input diagnostics and
the frozen research exclusions remain in the canonical report. Unknown or incomplete source quality
blocks the evaluation while the research replay result and reasons remain
visible. No promotion thresholds or real OOS conclusions are supplied by the
synthetic fixtures.

P14-M02-T002 assembles the App-level promotion evidence bundle from this
frozen report and the linked typed snapshots. It retains the source manifest,
quality and reconciliation references, causal fields, provenance, exclusions,
unknown values, and unresolved blockers. A supplied Lane C quality report or
shadow bundle must carry an identity referenced by the frozen source records;
the research simulation snapshot must preserve its canonical report,
configuration link, stream identity, and model-version reference. Native
identities are stored separately from snapshot content hashes so an opaque
producer identity is never replaced or inferred.

The bundle always makes data-quality, reconciliation, research shadow,
research simulation, operational shadow, local-paper, and exchange-test
categories explicit. Synthetic research shadow/simulation artifacts remain
separate from operational proof, and missing operational categories remain
unknown or blocked. Bundle restore verifies canonical content and
`ValidateAgainst` reports changed OOS context or evidence as invalidated
without mutating the prior record. Promotion review and the final gate remain
the next P14-M02-T003 task.
