# Price-context v2

`TradingBot.Core.PriceContextFeatureCalculator` exposes a pure
`Calculate(input, cursor, configuration)` contract. The fixed algorithm
version is `price-context-v2`. It reads no clock, network, persistence, or
strategy state and produces only deterministic feature values and provenance.

## Causal histories and provenance

The input contains independent closed-bar histories for setup and HTF
timeframes. Each history carries its own explicit timeframe and
`HistoryStart`; the calculator never selects, aggregates, or synthesizes HTF
bars. Caller-supplied price-volume observations are separate from both
histories.

Bars and observations retain event time, receive time, availability time, and
stable processing sequence. A value is eligible only when all causal bounds
are at or before the supplied `CausalEventCursor`. Future, late, duplicate,
conflicting, pre-history, and gapped values remain visible through stable
quality reasons. Duplicate payloads are collapsed deterministically;
conflicts and gaps block numeric readiness. Canonical ordering uses ordinal
identity fields and exact decimal values, so input permutations and current
culture do not change results.

## Explicit configuration

The configuration has no inferred periods, multipliers, timeframes, or enum
values. It explicitly selects:

| Group | Fields |
| --- | --- |
| Structural pivots | positive `PivotLeft`, `PivotRight`, `AtrPeriod`, `AtrMode`, `LevelBufferMultiplier` |
| Local channel | `CenterKind`, `CenterPeriod`, optional HLC3 smoothing (`EMA` or `RMA`), `WidthKind`, `ChannelWidthPeriod`, `ChannelWidthMultiplier` |
| HTF context | `HtfFastPeriod`, `HtfSlowPeriod`, MA kind, and confirmation count `1..3` |
| Volume features | optional `VwapWindow`, `PocWindow`, and positive `PocBinSize` |

Missing, negative, overflow-sized, unsupported, or mismatched configuration
values produce feature-local `UnknownConfiguration` output with stable
diagnostics. Checked arithmetic produces `UnknownOverflow`; no numeric
sentinel represents an unknown result.

Pivot identification is independent from ATR buffer calculation: when the
left/right window is valid but ATR buffer configuration is missing, confirmed
pivots remain `Ready` and expose nullable ATR/buffer evidence. Structural
levels still require a valid ATR mode, period, and level multiplier and remain
`UnknownConfiguration` until those fields are supplied.

## Strict pivots and structural levels

A low pivot is strictly below every available neighbor in its left and right
window. A high pivot is strictly above every neighbor. Equal extrema and
plateaus are not pivots. A pivot is unavailable until its complete right
window has closed and all observations are available. The result retains the
pivot bar, complete window, left/right periods, confirmation close time,
availability time, stable sequence, event and receive times, and the ATR
value used for its level buffer.

The latest confirmed low is support and the latest confirmed high is
resistance. A pivot whose confirmation-time history cannot produce the
configured ATR has no buffer; its evidence remains visible with nullable
ATR/buffer, while structural levels report `UnknownWarmup`. No zero buffer is
substituted. The buffer is frozen as `ATR * LevelBufferMultiplier` at pivot
confirmation. Existing levels are checked on each closed-bar close before
pivots confirmed on that close are registered. Support breaks only when
`close < anchor - buffer`; resistance breaks only when
`close > anchor + buffer`. Equality and wick-only moves do not break a
level. A break changes the same level's role while retaining its anchor,
buffer, and provenance. New confirmed lows replace support and new confirmed
highs replace resistance. A broken source slot is cleared and the opposite
slot receives the broken level. Inverted active levels are `UnknownQuality`.

Structural transitions, channel inputs, and ATR windows are ordered by causal
availability and stable sequence. A delayed bar enters the numeric series when
it becomes available; it is not inserted into an earlier point in the series
or used to rewrite an already replayed transition.

## Local channel and trend

The center is an explicit EMA or RMA of close, or HLC3 (`(high + low + close) /
3`) smoothed by the caller-selected EMA or RMA. Width is either ATR times its
explicit multiplier or population standard deviation of close times its
explicit multiplier. Width has its own period. Population standard deviation
uses a deterministic decimal square root implemented with BCL `BigInteger`
and rounds to the nearest representable decimal with ties to even.

Local trend uses the current closed bar and the current and prior centers:

* Bull when current center is greater than prior center and close is at least
  current center;
* Bear when current center is lower than prior center and close is at most
  current center;
* Neutral otherwise.

## HTF context

On each distinct closed HTF close, a candidate is Bull when
`close > support` and `FastMA > SlowMA`, Bear when `close < resistance` and
`FastMA < SlowMA`, and Neutral otherwise. A missing direction-relevant level
is `UnknownWarmup`. The explicit confirmation count is the number of
consecutive distinct closed bars required, including bars with the same close
price. A duplicate copy of one bar is deduplicated and does not increment the
count. A candidate change starts at one, unknown resets the count to zero, and
the last confirmed regime is retained. Before the first confirmation,
confirmed regime is null with pending candidate/count exposed.

The EMA, RMA, true-range, and decimal mean kernels are shared with
`RollingIndicatorCalculator`; their operation order remains unchanged for
`rolling-v1` consumers. Price-context contracts live separately from the
calculator, and population standard deviation uses its own deterministic
`BigInteger` square-root routine.

## Exact price-volume features

VWAP and POC consume only the exact caller-supplied price-volume observations.
OHLCV bars are never converted into a typical-price substitute. VWAP uses the
configured observation window and exact price-volume products. POC uses the
configured positive bin size and deterministic lower-bin tie breaking.

All outputs retain state, nullable values, warmup counts, completeness,
canonical provenance, and stable reasons. Strict `CoreJsonSerializer` output
round-trips without unknown properties. This contract adds no strategy,
risk, execution, ranking, or persistence behavior.
