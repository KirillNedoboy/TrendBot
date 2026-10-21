# Boundary Rejection (Lane A)

`TradingBot.Core.BoundaryRejectionSetupEvaluator` is the pure
`boundary-rejection-v1` implementation of the first V1 Lane A setup family. It
consumes one immutable `price-context-v2` result and its `feature-quality-v1`
evidence, and emits one long and one short observation at the same causal
cursor. It reads no clock, performs no I/O, and cannot create a lifecycle
transition, risk decision, order, or execution state.

## SETUP-01 mapping

| Canonical condition | Implementation |
|---|---|
| `HTF Context = BULL` / `BEAR` | `PriceContextHtfContext.ConfirmedRegime` |
| `Local Trend = BULL` / `BEAR` | `PriceContextLocalTrend.Direction` |
| LONG `Low <= LowerBand` | `SignalBar.Low <= PriceContextChannel.Lower` |
| LONG `Close > LowerBand` | `SignalBar.Close > PriceContextChannel.Lower` |
| SHORT `High >= UpperBand` | `SignalBar.High >= PriceContextChannel.Upper` |
| SHORT `Close < UpperBand` | `SignalBar.Close < PriceContextChannel.Upper` |
| `bar confirmed` | closed `ScreenerBar` that is eligible at the cursor, is the latest channel provenance bar, and is the local-trend current bar |
| `minimum channel width passed` | caller-supplied `MinimumChannelWidth` compared inclusively with `PriceContextChannel.Width` (`Upper - Lower`) |
| `data quality passed` | exactly one ready `FeatureQualityEvidence` for Channel, LocalTrend, and HtfContext, with matching formula, original state, and input series |

The additional filters named by the specification (`volume`, `POC`,
`OrderBlock`, Lane B, Lane C) are not required by SETUP-01 and are not
implemented here. Their evidence may remain unknown without blocking this
family. Structural pivots and dynamic levels are consumed through the
price-context result, but they are not SETUP-01 gates. A pending HTF candidate
never satisfies the confirmed `HTF Context` condition.

## Inputs and explicit configuration

`BoundaryRejectionSetupConfiguration` has two caller-supplied fields:

- `AlgorithmVersion`, which must be `boundary-rejection-v1`;
- `MinimumChannelWidth`, which must be a positive decimal.

There are no numeric defaults, TTLs, weights, or fallback thresholds. The
price-context result must carry `price-context-v2`; the quality evaluation must
use the same `CausalEventCursor` as the price-context result. An unsupported or
missing version, a missing or non-positive minimum width, or a mismatched
quality cursor is handled as explicit unknown evidence (the cursor mismatch is
a caller error and throws `ArgumentException`).

The canonical specification calls the one-sided ATR offset in the channel
formula `width` (`upper = center + width`). The existing channel contract exposes
that offset as `HalfWidth` and the full band distance as `Width`. SETUP-01 does
not define which measure the minimum refers to. This implementation compares
the full `Upper - Lower` span against the caller threshold, inclusively; both
`ChannelHalfWidth` and `ChannelWidth` remain in the observation evidence so the
decision is auditable. The threshold is still entirely caller-owned.

## Rules

LONG is ready only when all of the following hold on the same confirmed bar:

```text
confirmed HTF regime = BULL
local trend = BULL
bar low <= lower band
bar close > lower band
bar is confirmed and causally eligible
channel width >= configured minimum
required data-quality evidence is ready and consistent
```

SHORT mirrors the rule:

```text
confirmed HTF regime = BEAR
local trend = BEAR
bar high >= upper band
bar close < upper band
bar is confirmed and causally eligible
channel width >= configured minimum
required data-quality evidence is ready and consistent
```

`BoundaryRejectionConditionEvidence` exposes every literal condition as a
separate boolean. `CloseReclaimed` is strict (`>` for long, `<` for short).
Equality with the band does not confirm a rejection and does not invalidate it.

## States and invalidation

`BoundaryRejectionSetupState` is a per-cursor family state, not the Phase 8
lifecycle state machine:

| State | Meaning |
|---|---|
| `Ready` | every required SETUP-01 condition passed |
| `NoSetup` | evidence was usable, but at least one condition did not pass |
| `Invalidated` | context, trend, boundary touch, confirmed bar, width, and quality passed, but the bar closed strictly through the band |
| `UnknownConfiguration` | setup or price-context version/configuration is missing or unsupported |
| `UnknownInput` | the signal bar is missing or not eligible at the cursor |
| `UnknownWarmup` | the channel, local trend, or HTF context is still warming up |
| `UnknownQuality` | incomplete/inconsistent quality evidence, stale data, or inconsistent feature provenance |
| `UnknownOverflow`, `UnknownZeroDenominator` | propagated numeric failure from price-context |

`Invalidated` is not evidence of a lifecycle transition and is not persisted in
this task. A strict close below the lower band (long) or above the upper band
(short) is the only invalidation case; equal-to-band closes are `NoSetup`.
`Forming` is also deferred: it requires live/intrabar or lifecycle semantics that
SETUP-01 does not define.

Unknown states win over definite rule outcomes. When multiple unknown causes are
present, the evaluator reports the most fundamental one in this fixed order:
configuration, input, overflow, zero denominator, warmup, quality. The reasons
list retains every detected cause, sorted ordinally and deduplicated.

## Evidence and causality

`BoundaryRejectionSetupResult` retains the complete price-context result, quality
evaluation, explicit configuration, and both observations. Each observation
retains the direction, state, signal bar, channel values, local-trend direction,
confirmed/pending HTF evidence, the configured minimum width, all condition
booleans, and stable reasons. The result is immutable and round-trips through
`CoreJsonSerializer` with unknown members rejected.

The signal bar is the exact `ScreenerBar` used as the local-trend current bar and
the latest channel provenance bar. It must be eligible under the inclusive
causal cursor (event time, availability time, stable sequence, and close time).
A future or unavailable bar cannot be the signal bar; price-context keeps such
input visible as an incompleteness/future reason, and the setup remains
fail-closed. Receive time is never substituted for event time. No wall clock,
ranking, persistence, identity, or order state is introduced.

## Boundaries

This task adds only the passive Lane A family evaluation. Center Retest,
versioned setup evidence persistence, lifecycle stages, Decision Engine, Risk
Engine, private execution, and real-money activation remain later tasks. The
result cannot imply an order by itself.
