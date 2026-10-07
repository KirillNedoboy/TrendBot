# ADR-012 — Frozen out-of-sample promotion gates

## Context

Backtest and paper results can be made to look better by changing the dataset, parameters, fill assumptions, or selection rules after seeing outcomes. A live transition also requires evidence from several independent lanes.

## Decision

Freeze the out-of-sample dataset, configuration/version identifiers, selection snapshot, and fill/risk assumptions before evaluation. Declare the promotion questions and acceptance evidence before running the evaluation. Promotion requires reproducible replay, data-quality and reconciliation evidence, shadow and separate local-paper/test-environment evidence, and explicit review. The plan records unknown thresholds as unknown; it does not invent numeric limits or authorize real orders automatically.

## Alternatives considered

Tune against the full dataset, select a gate after observing returns, or use a single paper run as proof of readiness. These approaches create lookahead and selection bias and collapse distinct safety concerns.

## Consequences

Every promotion report must identify frozen inputs, criteria, exclusions, and unresolved blockers. A changed risk model, fill model, data source, or execution adapter invalidates the affected evidence and sends the work back through the relevant gate.
