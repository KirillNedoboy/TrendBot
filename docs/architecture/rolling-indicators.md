# Rolling indicators

`TradingBot.Core` contains a pure rolling-indicator calculator for one
instrument and one timeframe. `RollingIndicatorConfiguration` requires an
explicit timeframe, `rolling-v1` algorithm version, ATR period and mode,
moving-average period and kind, and RVOL period. Missing, non-positive,
unsupported, or mismatched configuration produces `UnknownConfiguration`;
there are no numeric defaults, weights, thresholds, TTLs, or wall-clock
reads.

## Causal input

`RollingIndicatorInput` carries the source, instrument, timeframe, an explicit
`HistoryStart`, and closed `ScreenerBar` observations. The calculator applies
the inclusive `CausalEventCursor` before any numeric operation. An observation
is eligible only when its event time, availability time, stable sequence, and
closed-bar time are at or before the cursor. Data before `HistoryStart` is
excluded with a provenance reason. Future, unavailable, and late observations
cannot change a result calculated at an earlier cursor.

The input and eligible observations are canonicalized with ordinal and exact
numeric ordering. Exact duplicate payloads collapse to one deterministic row.
Conflicting observations for one bar and gaps larger than the configured
timeframe are retained as reasons and block numeric readiness; gaps are never
filled. Event time, receive time, availability time, stable sequence, and
close time remain separate in the retained bar provenance.

## Formulas and lookback

All arithmetic is checked `decimal` arithmetic. A decimal overflow, zero RVOL
denominator, quality conflict, or gap yields a nullable value with an explicit
unknown state and reason.

| Indicator | Required bars | Formula |
|---|---:|---|
| SMA | `N` | Mean of the last `N` closes |
| WMA | `N` | Last `N` closes weighted `1..N` from oldest to newest |
| EMA | `N` | Mean seed of the first `N` closes, then `previous + 2/(N+1) * (close - previous)` through the full history |
| RMA | `N` | Mean seed of the first `N` closes, then `previous + (close - previous)/N` through the full history |
| ATR SMA | `N + 1` | True range for each bar, then mean of the latest `N` true ranges |
| ATR RMA | `N + 1` | Mean seed of the first `N` true ranges, then the RMA recurrence |
| RVOL | `N + 1` | Last volume divided by the mean volume of the preceding `N` bars |

True range is
`max(high-low, abs(high-previousClose), abs(low-previousClose))`. The first
bar uses its own close as `previousClose`; the extra bar in the ATR warmup is
therefore explicit. The RVOL denominator uses base bar volume and remains
unknown when its preceding sum is zero.

## Provenance and replay

`RollingIndicatorResult` retains the original input, cursor, configuration,
canonical eligible membership, quality reasons, nullable values, periods,
required and available bar counts, and per-indicator seed/provenance bars.
Recursive MA values are seeded from the full eligible history beginning at the
explicit `HistoryStart`; seed bars are retained separately so a replay can
explain the first state. ATR and RVOL provenance includes the preceding-close
or preceding-volume bar required by their lookback.

The result is immutable and serializes through the strict Core JSON contract.
Canonical input ordering, ordinal reason ordering, checked decimal formulas,
and pure replay at the same cursor make repeated calculations and JSON round
trips deterministic. This task adds no levels, channels, setups,
subscriptions, persistence, or execution behavior.
