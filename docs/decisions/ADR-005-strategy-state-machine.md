# ADR-005 — Strategy state machine

## Context

Setups move through observation, formation, confirmation, arming, entry, exit, and cooldown. Duplicate events must not create duplicate entries.

## Decision

Represent setup lifecycle as an explicit deterministic state machine with stable setup identity, reason codes, legal transitions, and terminal states.

## Alternatives considered

Boolean flags in strategy code, implicit transitions from mutable objects, or a workflow engine.

## Consequences

State-machine and failure-injection tests become mandatory. Setup persistence and replay must preserve identity and transition history.
