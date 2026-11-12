# Passive signal scenario lifecycle

`SignalScenarioAnchor`, the versioned scenario policies, and `SignalScenarioProjectionEngine` define a BCL-only pure projection. They do not evaluate setups, read market data, persist state, publish alerts, or create risk, order, fill, or position records. Durable storage and runtime composition remain outside SR-01.3.

## Identity and anchor

A confirmation must reference a structural anchor that causally precedes it: anchor event time is earlier, or event times tie and anchor stable sequence is lower. The anchor's event ID is supplied as `SignalIdentity.SetupEventId`; it is the identity seed, not the later confirmation event. `SignalId` therefore remains stable when anchor timestamps/sequences, confirmation availability, restart/run metadata, or publication time change. These clocks/provenance remain on causal inputs and immutable transition events, never in the logical signal ID. A rearm uses a new structural anchor event ID and consequently a new `SignalId`.

## Required versioned policies

`SignalScenarioPolicySet` requires explicit versioned expiry, rearm, and invalidation policy objects. Expiry duration and rearm delay may be represented as unknown; missing/unknown mandatory values, or a rearm policy that does not require a new anchor, make `IsReady` false and projection returns `UnknownPolicy` without consuming input or creating an alert. No numeric strategy or lifecycle threshold is supplied by Core.

The configured expiry duration starts at the first alert event. Rearm delay is measured from the prior episode's effective terminal time; equality at the delay boundary is eligible. Explicit invalidation and opposite-direction cancellation behavior are independent fields of the versioned invalidation policy. Cross-family overlap matching is fixed by the versioned projection algorithm.

## Transition table

| Input / condition | Result | Immutable events |
|---|---|---|
| Same stream/event ID and identical content | `Duplicate`; snapshot unchanged | none |
| Same stream/event ID with different content | `IdentityConflict`; snapshot unchanged | none |
| Input earlier than the retained event/availability cursor or with a non-advancing stable sequence | `Late`; snapshot unchanged | none |
| First valid confirmation for an anchor | Active episode | `AlertCreated` |
| New confirmation for the same active `SignalId` | Continue existing episode; never create another alert | `Continued`, linked to original alert event |
| New anchor while same family/direction is active | Reject rearm | none |
| Confirmation of opposite direction when policy cancels | Cancel same-instrument, same-family opposite episode, then create the new episode | linked `Canceled`, then `AlertCreated` |
| Explicit invalidation when policy cancels | Close the addressed active episode | linked `Canceled` |
| First ordered input at/after an expiry boundary | Close due episodes before processing that input | linked `Expired`; `EffectiveAt` is the expiry boundary |
| Rearm after terminal state, new anchor, and elapsed delay | Create a distinct episode and logical signal ID | new `AlertCreated` |
| Different setup family, same instrument/direction, with intersecting active intervals | Keep both episodes active | canonical `OverlapLinked` pair with both alert-event IDs |

Expiry is event-driven: wall-clock passage alone does not mutate the projection. The first later causal input recognizes expiry using `ExpiresAt <= input.EventTime`; the expiry event retains that input's event/availability/sequence and separately records the effective expiry boundary. Overlap intervals are half-open `[CreatedAt, ExpiresAt)`, so an episode expiring at the incoming confirmation boundary is expired before overlap evaluation.

## Immutable events and recovery

Every transition event has a content-derived event ID, source stream/event identity, causal timestamps/sequence, policy versions, and scenario links. `Canceled` and `Expired` retain both the logical `SignalId` and original `AlertCreated` event ID; the original alert is never rewritten. `OverlapLinked` stores both logical IDs and both originating alert-event IDs. Inputs and emitted events remain in the immutable projection snapshot, which includes the causal cursor and policy set. `Replay(snapshot, suffix)` is pure and has no I/O or timers; serialization/reopen plus suffix replay must equal uninterrupted projection over the same inputs.

The projection models passive alert episodes only. It contains no `Filled`, `PositionOpen`, execution, risk, or order transition.

## Behavioral verification

`SignalScenarioProjectionTests` covers unknown-policy blocking, exact duplicate and same-scenario continuation, anchor identity clock exclusions, late input, opposite cancellation, cross-family overlap, expiry/rearm boundary behavior, immutable alert links, strict contract validation, and continuous-versus-restored projection parity. Test-only durations are fixture inputs, not approved production policy values.
