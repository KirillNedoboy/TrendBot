# Setup lifecycle and deterministic transition boundary

P08-M01 implements the lifecycle as a pure Core state machine over immutable
external facts. It consumes Lane A `SetupEvidenceSnapshot` and opaque
observations supplied by later integrations. It does not calculate setup
evidence, risk, order quantities, exchange actions, or durable projections.

## Identity and causal input

`SetupLifecycleIdentity` is the lower-case SHA-256 fingerprint of the exact
ordered tuple `InstrumentId`, `SetupType`, `Direction`, `StructuralAnchorId`,
`AnchorTime`, and `ContextVersion`. The fingerprint is checked when a snapshot
is deserialized, so a changed anchor or context cannot reuse an old lifecycle
identity.

`SetupLifecycleInput` retains stream and event identity, event time, receive
time, availability time, stable processing sequence, rules/configuration
versions, provenance, and the immutable fact payload. Setup evidence is kept
as evidence; plan, order, position, and exit values are external identifiers
only. No input is interpreted as a permission to place an order.

## Legal transitions

| Fact | Required state | Result |
| --- | --- | --- |
| ContextReady | Observing | ContextReady |
| StructureFormed | ContextReady | SetupForming |
| Ready setup evidence | SetupForming | SetupConfirmed |
| Confirmation | SetupConfirmed | ConfirmationWindow or EntryArmed when explicitly configured |
| ExternalPlanReady | ConfirmationWindow | EntryArmed |
| OrderObserved | EntryArmed | OrderPending |
| PositionObserved | OrderPending | PositionOpen |
| ExitObserved | PositionOpen or ExitPending | Cooldown |
| CooldownElapsed | Cooldown | Terminal |
| StructureReset | Terminal and a new identity | Observing for the new identity |

Invalidated evidence and vetoes produce `Invalidated`; unknown or malformed
facts produce `Suspended` or a rejected transition. A Ready evidence snapshot
advances one lifecycle stage only. A new identity cannot bypass terminal state
and an explicit reset.

## Idempotency, ordering, and recovery

The engine records a compact receipt for every applied fact. An exact
stream/event identity and payload repeat returns `Duplicate` without a second
receipt. The same identity with different content returns `IdentityConflict`,
sets `RecoveryRequired`, and suspends further inputs until a `Recovery` fact
supplies a verified snapshot. Inputs earlier than the retained event time,
availability time, or stable sequence return `Late` and leave the state and
cursor unchanged.

`InstrumentLifecycleProcessor` locks the complete in-memory read-modify-write
for one instrument key. Distinct instruments use distinct locks and may run in
parallel. The optional commit callback is invoked while the key is locked;
returning false records `CommitRejected` and leaves the previous snapshot
visible. Durable persistence and restart projections are P08-M02 work.

## Observability and boundaries

`SetupLifecycleTransition` records previous/current state and identity, the
full canonical decision-input JSON, provenance, rules/configuration versions,
reasons, and opaque shadow/outcome observations. `DecisionEngine` consumes a
Ready Lane A snapshot plus explicit Lane B/C evidence and emits only the
existing `Decision` contract with `Allow`, `Block`, or `Shadow`. Vetoes,
unknowns, stale/future cursors, missing required evidence, and non-`EntryArmed`
lifecycle states fail closed. Risk decisions, order intents, positions,
execution planning, and exchange adapters remain separate contracts and are not
reachable from the lifecycle or decision result.
