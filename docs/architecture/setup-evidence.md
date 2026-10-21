# Versioned setup evidence

`SetupEvidenceSnapshot` is the Core bridge between a Lane A family observation
and the future Phase 8 lifecycle. It is per direction, immutable, canonical, and
versioned. It retains the complete causal input needed to reproduce the setup
decision and explicit invalidation evidence, but it contains no lifecycle,
decision, risk, order, or execution state.

## Schema

| Field | Meaning |
|---|---|
| `FormatVersion` | evidence payload format, currently `1` |
| `AlgorithmVersion` | evidence schema version, currently `setup-evidence-v1` |
| `SetupType` | the only V1 families: Boundary Rejection or Center Retest |
| `Direction` | `Long` or `Short` for this snapshot |
| `PriceContext` | complete `price-context-v2` result and its immutable input |
| `Quality` | complete `feature-quality-v1` evidence for that cursor |
| `BoundaryRejectionConfiguration?` / `CenterRetestConfiguration?` | exactly one family configuration |
| `BoundaryRejectionObservation?` / `CenterRetestObservation?` | exactly one selected-direction family observation with full conditions/reasons |
| `InputSequence` | compact causal provenance |
| `Invalidation` | explicit state/invalidation evidence |
| `SnapshotId` | lower-case SHA-256 content identity, excluded from JSON |

The second direction is not stored in this snapshot. It receives its own
`SetupEvidenceSnapshot`; the complete shared `PriceContext`/`Quality` still
provides the common causal input for both.

## Causal input sequence

`SetupEvidenceInputSequence` records:

- the inclusive `CausalEventCursor`;
- source, instrument, setup timeframe, and HTF timeframe;
- setup/HTF history starts;
- the exact `ScreenerBar` signal bar, if the selected observation has one;
- family algorithm version, price-context formula version (nullable when the
  configuration is absent), and quality algorithm version;
- derived, non-serialized event/receive/availability/close time and stable
  sequence from the signal bar.

The complete `PriceContextFeatureResult` retains every eligible bar/trade and
the exact cursor; the compact sequence is a validated summary, not a
replacement. Unknown or warmup evidence may have no signal bar; the cursor and
history provenance remain present.

## Invalidation

`SetupEvidenceInvalidation` records:

- `State`: the generic family evaluation state;
- `IsInvalidated`;
- `ClosedThrough`;
- ordinal-sorted, deduplicated `Reasons` from the selected observation.

`IsInvalidated` must equal `State == Invalidated`. `ClosedThrough` is true only
for invalidated evidence, and only when the family observation confirms
`ClosedThroughBoundary` (Boundary Rejection) or `ClosedThroughCenter` (Center
Retest). Raw close-through conditions on an unknown-quality observation remain
visible in the observation but do not become an authoritative invalidation.

The family reasons (`closed-through-lower-band`, `closed-through-upper-band`,
`closed-through-center`, no-setup and unknown-quality reasons) are preserved as
stable text. No separate lifecycle reason-code enum is introduced in this task.

## Identity and replay

The snapshot constructor:

1. validates format/algorithm versions, setup type, direction, and exactly one
   family configuration/observation pair;
2. requires `PriceContext.Cursor == Quality.Cursor == InputSequence.Cursor`;
3. matches `InputSequence` to the retained price-context input, signal bar, and
   version fields;
4. matches invalidation state, close-through, and reasons to the selected
   observation;
5. re-evaluates the selected family from the retained input and rejects any
   mismatch.

`SnapshotId` is the lower-case SHA-256 of the canonical `CoreJsonSerializer`
JSON with identity excluded. The identity is evidence-content identity, not the
Phase 8 `SetupId` or structural fingerprint. A future lifecycle can derive its
fingerprint from the retained type, direction, instrument, signal bar, and
version context.

`Replay()` re-evaluates the family from the retained `PriceContext`, `Quality`,
and configuration, rebuilds the snapshot, and fails if the identity changes.
This makes the evidence replayable without a clock, persistence, or external
data source.

## Boundaries

This task adds no SQLite/schema/store, lifecycle state machine, setup identity,
Decision Engine, Risk Engine, execution, order, or position state. It also adds
no persisted evidence retention policy. Those remain later tasks; the snapshot
is a passive, serializable Core contract and cannot imply an order by itself.
