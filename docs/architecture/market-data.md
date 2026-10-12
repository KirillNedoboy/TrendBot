# Market data architecture

The initial venue is Binance USD-M perpetual futures. The adapter owns Binance
REST and WebSocket protocol details and exposes normalized Core events. Public
market data is staged from exchange metadata and bars to trades, mark/funding,
open interest, liquidations, depth, and local orderbook recovery.

## Public REST contract (P04-M01-T001)

The endpoint matrix was checked against the current official USD-M market-data
catalog on 2026-10-12. All rows below use `https://fapi.binance.com`, require no
API key or signature, and preserve Binance response errors (including HTTP 429
and the JSON `code`/`msg`) as `BinanceApiException`. Request weights are the
documented IP weights; the client does not retry or silently substitute another
endpoint.

| Query | Path | Weight / limit | Normalized result |
|---|---|---|---|
| Exchange information | `/fapi/v1/exchangeInfo` | 1 | Immutable exchange, asset, symbol, filter, and rate-limit DTOs |
| Server time | `/fapi/v1/time` | 1 | `BinanceServerTime` with raw Unix milliseconds |
| Symbol price ticker | `/fapi/v1/ticker/price` | 1 for one symbol; 2 when omitted | Exact-decimal price ticker DTOs |
| 24-hour ticker | `/fapi/v1/ticker/24hr` | 1 for one symbol; 40 when omitted | Exact-decimal 24-hour ticker DTOs |
| Klines | `/fapi/v1/klines` | limit based; max 1500 | Raw twelve-field kline DTOs |
| Recent trades | `/fapi/v1/trades` | 5 | Recent trade DTOs |
| Aggregate trades | `/fapi/v1/aggTrades` | 20; max 1000 | Aggregate trade DTOs |
| Mark price / funding | `/fapi/v1/premiumIndex` | 1 for one symbol; 10 when omitted | Mark/funding DTOs and `FundingUpdate` |
| Funding history | `/fapi/v1/fundingRate` | 1 | Funding-rate DTOs and `FundingUpdate` |
| Funding info | `/fapi/v1/fundingInfo` | 1 | Funding-cap and interval DTOs |
| Current open interest | `/fapi/v1/openInterest` | 1 | Open-interest DTO and `OIUpdate` |

Official references: [USD-M market-data catalog](https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/rest-api/market-data), [exchange information](https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/Exchange-Information), [kline schema](https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/Kline-Candlestick-Data), [aggregate trades](https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/Compressed-Aggregate-Trades-List), and [mark price](https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/Mark-Price).

Wire DTOs are immutable and keep exchange timestamps as raw `long` Unix
milliseconds. Decimal values accept Binance's documented string representation
without rounding. Convenience methods map recent/aggregate trades to Core
`Trade`, closed klines to Core `Bar`, mark/history funding to `FundingUpdate`,
and current open interest to `OIUpdate`. A kline whose close time has not passed
the injected `TimeProvider` remains a wire DTO and cannot become a Core `Bar`.
The adapter has no strategy, retry, rate-limit policy, or private API behavior.

## Public WebSocket contract (P04-M01-T002)

The current WebSocket market-data routes are `wss://fstream.binance.com/public`
and `wss://fstream.binance.com/market`; this implementation uses the official
`/market` route. Stream subscriptions are expressed using Binance's live
`SUBSCRIBE`/`UNSUBSCRIBE` request shape and can be replayed after reconnect.
The transport layer keeps raw and combined envelopes, handles fragmented frames,
and leaves event-time, exchange-time, receive/availability-time, and the
session-scoped stable sequence distinct for Core replay.

Recovery is reconnect-and-resubscribe with explicit diagnostics. No REST depth
backfill or orderbook recovery is introduced by T002; those belong to T003.
