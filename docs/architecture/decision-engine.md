# Decision Engine boundary

The Phase 8 Decision Engine is a deterministic, side-effect-free Core boundary.
`DecisionEngineInput` contains the lifecycle snapshot, the retained Lane A
setup evidence, explicit Lane B/C evidence, and provenance. Every lane record
keeps its algorithm version and causal cursor.

The rule table is fail-closed:

| Input | Rule |
| --- | --- |
| Lane A | Must be `Ready`, match lifecycle identity, and be visible at the lifecycle cursor |
| Any lane | `Veto`, `Unknown`, stale, future, or conflicting evidence blocks |
| Required Lane B/C | A `Confirm` is required; `Neutral` is accepted only when its profile flag is explicit |
| Optional Lane B/C | Missing or `Neutral` is allowed only by the explicit profile; `Veto` still blocks |
| Lifecycle | `EntryArmed` and no recovery marker are required for Allow or Shadow |
| Profile | `Allow` emits `DecisionOutcome.Allow`; `Shadow` emits `DecisionOutcome.Shadow` |

The result retains the existing `Decision` and the complete immutable input
snapshot. It does not contain `RiskDecision`, `OrderIntent`, `Position`, sizing,
execution plans, or authorization state. Risk and execution remain separate
gates in later phases.

## Lane B handoff metadata

`DecisionEvidence` may carry one optional `LaneBDecisionEvidenceMetadata`
record. The metadata is created only from a validated `LaneBJoinSnapshot` and
retains the join identity, deterministic shadow evidence identity and version,
causal cursor, quality state, observation references, and stable quality
reasons. Its effective `DecisionEvidenceStatus` is the shadow status only when
quality is `Ready`; missing, stale, unavailable, conflicting, incompatible,
overflow, and unknown quality map to the existing `Unknown` status while their
details remain in the decision input snapshot JSON.

The metadata constructor and `DecisionEvidence` validate cursor, identity,
algorithm-version, lane, and effective-status agreement. A null metadata value
is omitted from canonical JSON, preserving the prior projection shape and
identity for legacy evidence. The handoff has no risk, order, execution, or
authorization surface; lifecycle `EntryArmed` and recovery gates continue to
be enforced by the existing Decision Engine.
