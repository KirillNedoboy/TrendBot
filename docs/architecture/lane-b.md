# Lane B causal inputs

`LaneBInput` is the passive Core snapshot for derivatives observations used by
the Lane B research boundary. It wraps the existing `OIUpdate` and
`FundingUpdate` payloads in the shared `CausalMarketEvent`, while retaining
source identity, event identity, instrument, explicit units, funding kind,
provenance, exchange time, local receive and availability times, and stable
sequence. Collections are copied on construction and serialized through the
strict Core JSON contract.

`LaneBInput.Select` first applies the inclusive event-time,
availability-time, and stable-sequence cursor. It then canonicalizes exact
identity repeats once and reports them as diagnostics. A same-identity payload
conflict blocks the selection. Caller configuration names the required units,
funding kind, and optional open-interest delta endpoints; no source, unit,
history, freshness, or trading threshold is inferred. The delta is the checked
absolute decimal difference between the two explicitly named compatible OI
observations. Missing, unavailable, incompatible, conflict, insufficient
history, arithmetic overflow, and unknown configuration states remain explicit.

The four focused Core fixtures cover exact decimal and causal-field retention,
strict JSON round trips, defensive collection snapshots, no-lookahead prefix
selection, explicit unknown/unavailable configuration, deterministic absolute
OI delta, exact duplicate diagnostics, and unit/identity conflicts. These
synthetic fixtures establish contract behavior only; they do not provide an
exchange freshness policy, real provider provenance, a failed-continuation
formula, or a Decision Engine handoff.

## Shadow behavior evidence

`LaneBLiquidationObservation` is a standalone causal record with source/event
identity, instrument, side, positive quantity, price, provenance, exchange
time, local receive/availability times, and stable sequence. It deliberately
does not extend the shared `MarketEvent` hierarchy. No liquidation rows are
treated as a zero count unless a later task supplies explicit coverage
evidence.

`LaneBFailedContinuationObservation` retains an externally declared outcome,
method version, causal links, and provenance. A missing method version, empty
causal links, or unknown outcome is insufficient data. The contract does not
invent a failed-continuation formula.

`LaneBEvaluator` accepts a versioned rule table keyed by setup type and
direction. Each rule names its metric conditions and outcome explicitly; the
evaluator only compares those declared values. OI delta alone cannot produce a
Confirm result. Missing rules, unavailable required metrics, conflicting
observations, missing liquidation coverage, and unknown behavior methods yield
`InsufficientData`. Results retain the `lane-b-shadow-v1` version, rule ID,
cursor, selected inputs, causal observation IDs, and stable reasons. This is
research evidence only and has no Decision Engine, RiskDecision, OrderIntent,
or authorization path.

## Freshness and replay evidence

`LaneBFreshnessPolicy` is supplied by the caller for each requested series:
open interest, funding, liquidation, and failed continuation. Missing and
non-positive TTLs produce `Unknown` and never produce ready evidence. For a
causally eligible observation, event age and availability age are calculated
separately against the supplied cursor. An age equal to its TTL is fresh; only
an age greater than the TTL is stale. `LaneBEvaluator` applies the policy to
the metric kinds named by the selected rule before evaluating the rule.

The existing `HistoricalReplayRunner` is the only runtime replay mechanism.
Replay fixtures build the Lane B input from each immutable cursor prefix and
feed the same evaluator. Input permutations and equal timestamps follow event
time plus stable sequence; future or delayed-availability observations stay
out of earlier prefixes. Exact repeats are counted once with a diagnostic and
same-identity conflicts block affected evidence. The fixtures demonstrate
deterministic synthetic behavior and do not establish provider coverage,
external chronology, real-data freshness values, or profitability.
