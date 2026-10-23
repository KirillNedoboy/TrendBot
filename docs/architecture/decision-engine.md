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
