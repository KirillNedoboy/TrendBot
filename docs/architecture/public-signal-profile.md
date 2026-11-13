# Public 15m/1h signal profile

`TradingBot.Core.PublicSignalProfileConfiguration` is a versioned, declarative configuration contract. Format version `1` versions the schema only; it is not a strategy parameter. The only supported bar source is public Binance USD-M native klines. `Setup` must identify `15m`; `Context` must identify `1h`. Neither role can select a private source or another timeframe.

The contract does not evaluate signals, access market data, read secrets, or authorize runtime use. `Ready` means only that every required configuration declaration is present. It is not signal, runtime, research, statistical, or trading readiness.

## Required declarations

| Section | Required fields |
|---|---|
| Profile | `profileVersion` |
| Each timeframe | `timeframe`, public `source`, `sourceVersion`, UTC `historyStartUtc`, `historyVersion`, `seedHistoryBars`, `seedVersion`, `warmupBars`, `warmupVersion`, `freshnessVersion`, `maximumMarketDataAge`, `maximumAvailabilityAge` |
| Formula versions | `priceContextVersion`, `featureQualityVersion`, `setupFamilyVersion` |
| Structure and ATR | `pivotLeftBars`, `pivotRightBars`, `atrPeriodBars`, `atrMode`, `atrValueUnit` |
| Local channel | `channelCenterKind`, `channelCenterPeriodBars`, `hlc3SmoothingKind`, `channelCenterValueUnit`, `channelWidthKind`, `channelWidthPeriodBars`, `channelWidthMultiplier`, `channelWidthValueUnit`, `channelWidthMultiplierUnit`, `levelBufferMultiplier`, `levelBufferMultiplierUnit`, `boundaryMinimumChannelWidth`, `boundaryMinimumChannelWidthUnit` |
| 1h context | `htfFastPeriodBars`, `htfSlowPeriodBars`, `htfMovingAverageKind`, `htfConfirmationBars`, `htfMovingAverageValueUnit` |
| Point-in-time universe | `selectionVersion`, `minimumSymbols`, `maximumSymbols` |

Period/count fields are measured in bars by their property names. Freshness ages are `TimeSpan` values and keep market-data age distinct from local availability age. Price-valued features and the Boundary Rejection minimum full-channel width require the explicit `Price` unit; multipliers require the explicit `Dimensionless` unit. History start must be UTC. Declared counts, ages, widths, and multipliers must be positive; minimum universe size cannot exceed maximum.

## Unknown and rejection behavior

Every strategy value, history boundary, warmup count, freshness limit, universe bound, and profile/formula/source version not supplied by an approved profile remains nullable. The contract supplies no trading defaults. Missing sections produce `indicator-configuration-unknown` / `universe-configuration-unknown`; missing individual fields produce stable `<field>-unknown` reasons. The singleton `PublicSignalProfileConfiguration.Missing` is an explicit `UnknownConfiguration`. Any missing reason blocks configuration readiness.

The schema accepts only the public native-kline source enum and the exact `15m` / `1h` roles. Unsupported schema versions, timeframes, enum values, units, malformed declarations, and unknown JSON members reject through `CoreJsonSerializer`; this includes private, activation, quantity, leverage, and portfolio properties. Configuration parsing has no credential or secret-store dependency.

This contract change supplied no complete profile or defaults. A later explicitly authorized T017 exploratory profile is separately frozen in `research-freeze.md`; it is not a validated or production profile.
