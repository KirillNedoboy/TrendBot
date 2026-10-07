# ADR-010 — Fail-closed policy and activation gate

## Context

Trading safety depends on fresh data, known positions/orders, valid risk configuration, and a functioning kill switch. Ambiguity can otherwise create unbounded exposure. Building private execution support or operating an exchange test environment is not the same as authorizing real money.

## Decision

Unknown, stale, inconsistent, or unreconciled state blocks new entries and marks the system for recovery. Exits and protective actions follow the independently verified safety path. Real-money activation remains a separate gate: required shadow, paper, data-quality, reconciliation, and incident evidence must be reviewed, and a user must explicitly command activation. No automatic promotion or live order activation is permitted.

## Alternatives considered

Fail-open for availability, retry forever without changing state, or let strategy decide safety. Automatic activation after a successful test-environment run was also rejected because it removes the human gate.

## Consequences

The system may miss opportunities during outages, but every block has an auditable reason. Shadow, local paper, exchange test, and limited-live gates must prove recovery before enabling the next lane. Data-quality and reconciliation status are part of the operational health signal.
