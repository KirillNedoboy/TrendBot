# Minimum screener feature contract

`P05-M02-T001` and `P05-M02-T002` define the passive, BCL-only minimum
screener surface in `TradingBot.Core`. It is a causal calculation boundary for
research and screening. It does not rank instruments, apply trading thresholds,
decide on a setup, persist snapshots, or authorize an order.

## Causal input and configuration

`ScreenerFeatureInput` carries source identity, instrument identity, event time,
local receive time, local availability time, and stable sequence for the input
snapshot. Its closed-bar, open-interest, and quote observations are immutable
snapshots, and each observation carries the same causal fields. `ScreenerBar`
requires a closed `Bar`, preserves the exchange-reported exact decimal quote
volume, and is eligible only when its `Bar.CloseTime` is at or before the
cursor's `AsOf`. Turnover never reconstructs quote volume from close and base
volume. `ScreenerQuoteObservation` records whether the bid/ask is ready; no
bid/ask default is supplied.

`ScreenerFeatureConfiguration` requires an explicit timeframe, formula version,
and positive lookback values for the features that use them. Missing or
non-positive values remain configuration state and produce
`UnknownConfiguration`; no numeric or timeframe default is introduced.

`ScreenerCausalSelection.Select` is the shared inclusive selection primitive. It
filters each observation by event time, availability time, and maximum stable
sequence, then orders eligible observations by event time and stable sequence.
The calculator applies the same rule before every formula and also applies the
closed-bar close-time bound. Event time and availability time remain separate;
receive time is provenance and is never substituted for event time.

## Freshness and completeness

Callers provide a `ScreenerFreshnessPolicy`. It can supply a default TTL,
feature-family TTLs (`BarTtl`, `QuoteTtl`, and `OpenInterestTtl`), or explicit
source TTLs. A missing TTL is represented as `ScreenerFeatureFreshness.Unknown`;
there is no numeric default. TTLs must be positive when present. The calculator
never reads a clock: it computes age as `cursor.AsOf - latest required
observation.AvailabilityTime` for each source used by that feature. Age exactly
equal to the supplied TTL is `Fresh`; only greater age is `Stale`. Every source
must be fresh for `ScreenerFeatureValue.IsReady` to be true.

Each feature retains a nullable decimal `Value`, a
`ScreenerFeatureCompleteness` value, a `ScreenerFeatureFreshness` value, and an
immutable `Reasons` list. `State` continues to identify formula validity and
warmup for compatibility with T001; `IsReady` is the effective gate and
requires a complete formula, valid inputs, and fresh inputs. A numeric value
may remain observable when freshness is stale, while `IsReady` remains false.

## Deterministic quality handling

Before calculating a formula, eligible observations are canonicalized in
ordinal deterministic order. Exact duplicates for the same observation key are
counted once. Same-key payload conflicts are removed and add the stable
`conflict` reason. Bars are keyed by source, instrument, and open time;
quotes and open interest use source, instrument, event time, and stable
sequence. Bars with missing timeframe intervals add `bar-gap` and are not
interpolated or filled. A future close or an event that is causally late by
availability remains explicit through `bar-close-after-cursor` or
`late-availability` reasons.

The quality pass is pure and culture independent. Reasons use ordinal strings,
and observation order is based on event time, stable sequence, source, and
availability. A conflict or bar gap produces `UnknownQuality` for the affected
feature. Missing observations remain `UnknownWarmup` or `UnknownInput`; no
missing value is synthesized.

## Formula contract

The pure `ScreenerFeatureCalculator.Calculate(input, cursor, configuration)`
returns exactly six named `ScreenerFeatureValue` values. All arithmetic uses
`decimal` without trading rounding.

| Feature | Required eligible inputs | Formula |
|---|---:|---|
| RVOL | `N + 1` closed bars | Last bar base volume divided by the mean base volume of the prior `N` bars |
| Price acceleration | `2L + 1` closed bars | `(C₂/C₁ - 1) - (C₁/C₀ - 1)` for adjacent `L`-bar close-return endpoints |
| ATR expansion | `2N + 1` closed bars | Mean of the latest `N` true ranges divided by the preceding `N` true-range mean, minus one |
| Turnover | `K` closed bars | Exact sum of the bars' quote-volume fields |
| Spread | one eligible ready quote | `(ask - bid) / ((ask + bid) / 2)` |
| OI anomaly | `M + 1` eligible observations | Current OI divided by the mean of the prior `M` observations, minus one |

Each true range is `max(high-low, abs(high-previous close),
abs(low-previous close))`. The first bar in the ATR input supplies the previous
close for the first true range, which is why the ATR warmup is `2N + 1` bars.

Insufficient eligible data returns `UnknownWarmup`. A zero denominator or
decimal arithmetic overflow returns an unknown value with a stable reason;
valid values retain the exact decimal result. The result carries the selected
formula version and the complete immutable input/cursor provenance for replay
and later snapshot work.

## Versioned screener snapshots

`ScreenerSnapshot` is the immutable format-one Core record for one calculated
result. It retains the explicit `ScreenerFeatureConfiguration` (including
nullable lookbacks and caller-supplied TTL policy), `CausalEventCursor`,
normalized `ScreenerFeatureInput`, and all six feature values with their
completeness, freshness, and stable reasons. Its formula version is a separate
field from the record format version; the current supported formula is
`screener-v1`.

The snapshot identity is the lower-case hexadecimal SHA-256 of one canonical
Core JSON representation with the identity and derived convenience properties
excluded. Construction is the single normalization path: observation lists are
ordered by their complete causal/value keys, features are ordered by kind, and
reasons are ordinally ordered. The calculation has no wall-clock input.

`SqliteScreenerSnapshotStore` persists that canonical JSON with the 32-byte
identity in a BLOB primary key. Schema migration 6 -> 7 adds only the
`screener_snapshots` table and preserves the event journal, intent,
reservation, operational, and capture rows. Append is idempotent for an exact
identity/content retry and commits before reporting success. Reads verify the
row metadata, canonical JSON, supported format/formula versions, and recomputed
identity; malformed or corrupted rows fail closed. The persisted input can be
re-evaluated with the pure calculator to reproduce the feature values.

## Scope boundary

Focused Core and Infrastructure fixtures cover snapshot identity determinism,
canonical round trips, format/formula rejection, corruption, close/reopen,
schema migration, rollback, and result re-evaluation. The existing focused
Core fixtures cover formula golden values, exact warmup boundaries,
cursor filtering and no-lookahead, causal bar close handling, caller TTLs,
availability-based freshness, exact TTL boundaries, stale and unknown
freshness, missing bar/quote/open-interest inputs, duplicate and conflicting
observations, bar gaps, culture invariance, decimal overflow, quote-volume
exactness, defensive collection snapshots, and read-only public contract
properties. All three staged-selection gates and their replay chain are
documented in [`staged-selection.md`](staged-selection.md); persistence and
execution remain later tasks.
