# Passive Lane A setup observations

`LaneASignalEvaluator` produces one shared price-context and feature-quality evaluation, then the existing `BoundaryRejectionSetupEvaluator` (SETUP-01) and `CenterRetestSetupEvaluator` (SETUP-02) results. `LaneASetupObservationProjection` combines those exact results from the same context and quality snapshot. It does not duplicate setup formulas or quality rules.

## Per-cut states and overlap

The projection retains both original family results, including their condition evidence and quality reasons, and exposes one status for each family and direction:

| Existing evaluator state | Passive observation status |
|---|---|
| `Ready` | `Confirmed` |
| `NoSetup` | `NoSetup` |
| `Invalidated` | `Invalidated` |
| Any `Unknown*` state | `Unknown` |

Each eligible cut is classified independently. `IsComplete` is false if any of the four family/direction states is `Unknown`; the other family remains visible and is not promoted or suppressed. Same-direction overlap is `Confirmed` only when both families are confirmed, `NoOverlap` when their combined evidence rules out a joint confirmation, and `Unknown` when the remaining family could still confirm. Both original observations are retained rather than selecting a winning setup.

Required setup configuration and the existing Channel, LocalTrend, and HtfContext quality evidence remain gates inside their original evaluators. Unknown required evidence therefore remains `Unknown`, never `NoSetup` or `Confirmed`. Optional Vwap/POC features and Lane B/C are not added as gates and have no `Neutral` state in this projection.

## Boundaries

This is a pure per-cut observation, not a stateful episode or persistence projection. It has no clock, I/O, signal journal, order, execution, risk, or position behavior. The separately versioned `SignalScenarioProjectionEngine` remains the owner of passive episode transitions; durable signal/scenario persistence is outside this task. See [setup family rules](boundary-rejection.md), [Center Retest rules](center-retest.md), [feature quality](feature-quality.md), and [passive scenario lifecycle](scenario-lifecycle.md).

Behavioral coverage lives in `LaneASetupObservationProjectionTests` alongside the existing SETUP-01/02 and feature-quality regressions.
