# Lane C shadow and decision evidence

`LaneCShadowEvaluator` is a pure BCL-only Core evaluator over a versioned
`LaneCFeatureResult` and an explicit immutable `LaneCShadowRuleTable`. Rule
tables carry their own version and may be empty; the project supplies no
numeric trading thresholds or default rules. Conditions identify a supported
feature metric, comparison, and caller-owned decimal value. Results retain the
feature input identity, rule version, causal cursor, matched rule identifiers,
traceable reason codes, and deterministic result identity.

The result status is Confirm, Neutral, Veto, or InsufficientData. Veto takes
priority over a simultaneous confirm. An unready feature result, empty rule
table, or absence of evaluable conditions returns InsufficientData. These
results describe shadow evidence only and do not create an order, reservation,
risk authorization, or execution side effect.

`DecisionEvidence.FromLaneCShadow` validates and carries the shadow result
identity, rule version, input identity, cursor, and reason codes. It maps
InsufficientData to the existing `DecisionEvidenceStatus.Unknown` and Veto to
`Veto`; Confirm and Neutral retain their corresponding evidence statuses. The
Decision Engine's existing required-evidence policy blocks Unknown Lane C
evidence. When the supplied evidence is allowed, the engine may produce a
Shadow decision, with no order or execution output.

Focused verification is recorded in `docs/TASKS.md` and `HANDOFF.md` for
P13-M01-T003.
