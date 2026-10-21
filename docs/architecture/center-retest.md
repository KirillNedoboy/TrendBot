# Center Retest (Lane A)

`TradingBot.Core.CenterRetestSetupEvaluator` is the pure
`center-retest-v1` implementation of the second V1 Lane A setup family. It
consumes one immutable `price-context-v2` result and its `feature-quality-v1`
evidence, and emits one long and one short observation at the same causal
cursor. It reads no clock, performs no I/O, and cannot create a lifecycle
transition, risk decision, order, or execution state.

## SETUP-02 mapping

| Canonical condition | Implementation |
|---|---|
| `HTF = BULL` / `BEAR` | `PriceContextHtfContext.ConfirmedRegime` |
| LONG `local bias = BULL` | `PriceContextLocalTrend.Direction = Bull` |
| LONG `Low <= Center` | `SignalBar.Low <= PriceContextChannel.Center` |
| LONG `Open >= Center` | `SignalBar.Open >= Center` |
| LONG `Close >= Center` | `SignalBar.Close >= Center` |
| LONG `Close > Open` | bullish body direction |
| SHORT `High >= Center` | `SignalBar.High >= Center` |
| SHORT `Open <= Center` | `SignalBar.Open <= Center` |
| SHORT `Close <= Center` | `SignalBar.Close <= Center` |
| SHORT `Close < Open` | bearish body direction |
| `bar confirmed` | closed `ScreenerBar` that is eligible at the cursor, is the latest channel provenance bar, and is the local-trend current bar |
| `data quality passed` | exactly one ready `FeatureQualityEvidence` for Channel, LocalTrend, and HtfContext, with matching formula, original state, and input series |
| minimum channel width | not a SETUP-02 condition; no gate or configuration is added |

The additional filters named by the specification (`volume`, `POC`,
`OrderBlock`, Lane B, Lane C) are not required by SETUP-02 and are not
implemented here. Their evidence may remain unknown without blocking this
family. Structural pivots and dynamic levels are consumed through the
price-context result, but they are not SETUP-02 gates. A pending HTF candidate
never satisfies the confirmed `HTF Context` condition.

### Explicit mirroring of the SHORT local bias

The canonical SHORT block omits the `local bias` line that the LONG block
contains. Because SETUP-02 is one long/short setup family and the project treats
SHORT as the mirror of LONG, this implementation requires
`LocalTrendDirection = Bear` for SHORT. This is the only interpreted condition;
no other unlisted rule is added. The condition is exposed in the observation
evidence, so the assumption is auditable.

## Inputs and explicit configuration

`CenterRetestSetupConfiguration` has only `AlgorithmVersion`, which must be
`center-retest-v1`. The family has no numeric parameters, TTLs, weights, or
fallback thresholds. The price-context result must carry `price-context-v2`;
the quality evaluation must use the same `CausalEventCursor` as the price-context
result. A missing or unsupported setup version produces explicit unknown
evidence; a mismatched quality cursor is a caller error and throws
`ArgumentException`.

The channel width, half-width, and bands are not consulted. This is deliberate:
SETUP-02 uses only the channel center. A test with a zero-width channel proves
that no minimum-width gate was imported from Boundary Rejection.

## Rules

LONG is ready only when all of the following hold on the same confirmed bar:

```text
confirmed HTF regime = BULL
local trend = BULL
bar low <= center
bar open >= center
bar close >= center
bar close > bar open
bar is confirmed and causally eligible
required data-quality evidence is ready and consistent
```

SHORT mirrors the rule:

```text
confirmed HTF regime = BEAR
local trend = BEAR
bar high >= center
bar open <= center
bar close <= center
bar close < bar open
bar is confirmed and causally eligible
required data-quality evidence is ready and consistent
```

`CenterRetestConditionEvidence` exposes every literal condition as a separate
boolean. `CenterTouched`, `OpenHoldsCenter`, `CloseHoldsCenter`, and
`CandleDirectionMatches` use the exact inclusive/strict comparisons above.
Equality with the center is allowed for touch, open, and close, but a close
equal to the center cannot produce a ready candle because the body-direction
condition still has to pass.

## States and invalidation

`CenterRetestSetupState` is a per-cursor family state, not the Phase 8 lifecycle
state machine:

| State | Meaning |
|---|---|
| `Ready` | every required SETUP-02 condition passed |
| `NoSetup` | evidence was usable, but at least one condition did not pass |
| `Invalidated` | context, trend, center touch, confirmed bar, and quality passed, but the bar closed strictly through the center |
| `UnknownConfiguration` | setup or price-context version/configuration is missing or unsupported |
| `UnknownInput` | the signal bar is missing or not eligible at the cursor |
| `UnknownWarmup` | the channel, local trend, or HTF context is still warming up |
| `UnknownQuality` | incomplete/inconsistent quality evidence, stale data, or inconsistent feature provenance |
| `UnknownOverflow`, `UnknownZeroDenominator` | propagated numeric failure from price-context |

For LONG, invalidation is `Close < Center` after a center touch; for SHORT it is
`Close > Center`. Equality with the center is `NoSetup`, not `Invalidated`.
`Invalidated` is not evidence of a lifecycle transition and is not persisted in
this task. `Forming` is deferred because it requires live/intrabar or lifecycle
semantics that SETUP-02 does not define.

Unknown states win over definite rule outcomes. When multiple unknown causes are
present, the evaluator reports the most fundamental one in this fixed order:
configuration, input, overflow, zero denominator, warmup, quality. The reasons
list retains every detected cause, sorted ordinally and deduplicated.

## Evidence and causality

`CenterRetestSetupResult` retains the complete price-context result, quality
evaluation, explicit configuration, and both observations. Each observation
retains the direction, state, signal bar, channel center, local-trend direction,
confirmed/pending HTF evidence, all condition booleans, and stable reasons. The
result is immutable and round-trips through `CoreJsonSerializer` with unknown
members rejected.

The signal bar is the exact `ScreenerBar` used as the local-trend current bar and
the latest channel provenance bar. It must be eligible under the inclusive
causal cursor (event time, availability time, stable sequence, and close time).
A future or unavailable bar cannot be the signal bar; price-context keeps such
input visible as an incompleteness/future reason, and the setup remains
fail-closed. Receive time is never substituted for event time. No wall clock,
ranking, persistence, identity, or order state is introduced.

## Boundaries

This task adds only the passive Lane A family evaluation. Boundary Rejection is
already implemented as a separate family; versioned setup evidence persistence,
lifecycle stages, Decision Engine, Risk Engine, private execution, and
real-money activation remain later tasks. The result cannot imply an order by
itself.
