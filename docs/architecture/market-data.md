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

## Timeframe-aware UTC klines (P15-M03-T005 / SR-02.2)

The Binance USD-M REST kline endpoint returns twelve positional fields:
`[0]` open time, `[1..5]` OHLC and base volume, `[6]` close time, `[7]` quote
volume, `[8]` trade count, `[9..10]` taker-buy volumes, and `[11]` `Ignore`.
The current official [machine-readable catalog](https://developers.binance.com/en/docs/llms-full.txt) lists `GET /fapi/v1/klines` and says rows are uniquely identified by open time. The directly linked current [REST schema](https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/Kline-Candlestick-Data) rendered only the documentation shell on 2026-11-09, so response positions and request fields below were cross-checked against Binance's official legacy [USD-M REST schema](https://developers.binance.com/legacy-docs/derivatives/usds-margined-futures/market-data/rest-api/Kline-Candlestick-Data). It documents `symbol`, `interval`, `startTime`, `endTime`, and `limit` (no `timeZone`) with the twelve-field row. The official legacy [USD-M WebSocket schema](https://developers.binance.com/legacy-docs/derivatives/usds-margined-futures/websocket-market-streams/Kline-Candlestick-Streams) confirms `i`, `t`, inclusive `T`, `E`, and `x`; the example's `T` is the last included millisecond.

`BinanceKlineIdentity` format 2 identifies `BINANCE`, `USD-M-PERPETUAL`,
canonical symbol, native timeframe, and open Unix milliseconds. Its v2 event
ID is timeframe-aware, so 15m and 1h bars at the same symbol/open are separate.
Previously persisted v1 event IDs are not rewritten or migrated; only newly
ingested bars use the versioned v2 identity.

Core-ready ingestion currently accepts the approved native `15m` and `1h`
intervals only. REST interval timestamps are UTC (the endpoint has no
`timeZone` request field); `open` must be non-negative, representable, and
aligned to the Unix-epoch UTC grid (`open % duration == 0`). Durations are
900,000 ms and 3,600,000 ms. The raw Binance `T` is retained as
`InclusiveCloseTime` and must equal `open + duration - 1`; normalized
`EndExclusiveTime` is `open + duration`. Core `Bar.EventTime` and `Bar.CloseTime`
use the normalized end-exclusive boundary, not raw `T`. An open WebSocket bar
or an invalid timeframe, grid, open, or close-duration produces an explicit
ingestion diagnostic and no Core `Bar`.

Each closed valid input has an accepted-revision record with identity, the
versioned `binance-usdm-kline-revision-v1` SHA-256, inclusive and end-exclusive
times, the Core bar, and source metadata. The hash covers open and inclusive
close times, exact-decimal OHLC/base volume/quote volume/taker-buy volumes, and
trade count; it excludes the Binance `Ignore` field and transport-specific
event/source metadata. Equal identity plus equal hash is the sole REST/WS
duplicate rule: return `Duplicate`, no second Core bar, and retain both source
records. Equal identity with a different hash is `RevisionConflict`; the
previous accepted revision remains authoritative and the conflicting payload
does not become Core input. Callers bridge REST and WebSocket views by supplying
the prior accepted-revision set to REST ingestion or the matching accepted
revision to WebSocket ingestion. This boundary is in-memory and does not add a
durable revision ledger; persistence and restart recovery remain later work.

## Exchange eligibility metadata (P05-M01-T001)

The metadata source is only `GET https://fapi.binance.com/fapi/v1/exchangeInfo`.
The official market-data catalog lists IP request weight 1, with no API key or
signature requirement. The response `serverTime` field is retained as wire data,
but the official schema says to use `/fapi/v1/time` for server time; it is never
used as metadata provenance. The adapter records separate local receive and
availability timestamps around the HTTP request and maps every symbol to the
venue-neutral Core `ExchangeEligibilityMetadata` contract.

The symbol contract retains identity (`symbol`/`pair`), contract and status,
base/quote/margin assets, onboarding and delivery timestamps, exact decimal
margin and filter values, precision fields, official symbol metadata such as
`underlyingType`, `underlyingSubType`, `settlePlan`, `triggerProtect`,
`liquidationFee`, and `marketTakeBound`, plus raw unknown symbol fields. The
wire filter DTO retains `notional` and `multiplierDecimal` and stores unknown
filter fields/types as raw JSON. Core mapping sorts unknown field names with
ordinal comparison so extension data remains deterministic.

The documented symbol filters include `PRICE_FILTER` (`minPrice`, `maxPrice`,
`tickSize`), `LOT_SIZE` (`minQty`, `maxQty`, `stepSize`), `MIN_NOTIONAL`
(`notional`), and `PERCENT_PRICE` (`multiplierUp`, `multiplierDown`,
`multiplierDecimal`). The common-definition documentation states that zero
values disable the corresponding `PRICE_FILTER` rule. No tick or step value is
derived from `pricePrecision` or `quantityPrecision`. Metadata freshness and TTL
remain `UNKNOWN` until a later task supplies an explicit policy.

Official sources checked 2026-10-16: [USD-M market-data catalog](https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/rest-api/market-data)
and [USDⓈ-M common definitions](https://developers.binance.com/en/docs/products/derivatives-trading-usds-futures/common-definition).

## Metadata-only candidate filter (P05-M01-T002)

`MetadataCandidateFilter.Evaluate` consumes only the immutable Core metadata
contract. It includes a symbol only when `contractType` is `PERPETUAL`, `status`
is `TRADING`, and both `quoteAsset` and `marginAsset` are `USDT`. Comparisons and
symbol ordering use ordinal comparison. There is no clock, network, screener,
feature, ranking, pool-size, trade, or execution input.

Each included candidate must have one `PRICE_FILTER`, one `LOT_SIZE`, and one
`MIN_NOTIONAL`. Their exact decimal fields are checked for presence and valid
ranges; a zero `minPrice`, `maxPrice`, or `tickSize` is accepted because the
official definition says it disables that `PRICE_FILTER` rule. Present
recognized optional filters are validated as metadata, including
`MARKET_LOT_SIZE`, `PERCENT_PRICE`, `NOTIONAL`, `MAX_NUM_ORDERS`,
`MAX_NUM_ALGO_ORDERS`, and `MAX_NUM_ICEBERG_ORDERS`. An additional unmodeled
filter remains retained as `UNKNOWN` data and does not exclude a candidate by
itself.

Missing required filters, non-perpetual or non-trading values, non-USDT quote or
margin assets, malformed fields, and duplicate or conflicting filter entries
produce immutable exclusion reasons. Reasons are de-duplicated and ordered by
eligibility category, duplicate category, malformed category, missing category,
then required filter order and ordinal reason text. Duplicate symbols are also
reported deterministically; candidates and exclusions are returned in ordinal
symbol order. The filter does not assign freshness or TTL; those remain
`UNKNOWN`.

The official public endpoint documents IP request weight 1, no API key or
signature, and exchange errors including HTTP 429 with a JSON `code`/`msg` body;
the adapter preserves those errors as `BinanceApiException` and does not infer a
retry or freshness policy. These facts and the filter semantics were checked
2026-10-16 against the official sources above.

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

## Depth snapshot and diff contract (P04-M01-T003)

The official USD-M depth contract was checked on 2026-10-12. REST snapshots use
`GET https://fapi.binance.com/fapi/v1/depth` with a required symbol and one of
the documented limits `5`, `10`, `20`, `50`, `100`, `500`, or `1000`. The
adapter preserves `lastUpdateId`, exchange event time `E`, transaction time
`T`, and exact decimal bid/ask pairs. HTTP receive and availability timestamps
remain separate local causal fields.

Depth WebSockets use the routed `wss://fstream.binance.com/public/stream`
endpoint. The existing market client remains on `/market`; it rejects depth
subscriptions so the two route contracts cannot be mixed. Depth stream names
are `symbol@depth`, `symbol@depth@100ms`, or `symbol@depth@1s`. Raw or combined
messages preserve `e`, `E`, `T`, `U`, `u`, optional `pu`, symbol, exact decimal
levels, source JSON, session ID, receive/availability timestamps, and a stable
session sequence. A fresh BCL `ClientWebSocket` transport is created for each
reconnect through an injectable factory.

`BinanceLocalOrderBookRecovery` buffers updates under a configured count bound,
loads a bounded REST snapshot, discards updates whose final ID is at or before
the snapshot, and aligns the first usable update when `U <= L + 1` and
`u >= L + 1`. Once aligned, a `pu` value must equal the current book update ID
and ranges may not skip an update ID. A gap, rejected snapshot, exhausted
recovery attempt bound, or buffer overflow clears the mutable working book and
publishes an immutable `Recovering` state with no levels; callers never observe
a partially applied book. Ready states are read-only snapshots sorted by price
and retain snapshot/update provenance, raw exchange times, local causal times,
and the last stable sequence. Duplicate, stale, late, parse, disconnect, gap,
frame, recovery, and snapshot diagnostics remain observable.

Official references checked for this contract: [depth REST](https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/Order-Book), [diff depth streams](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/Diff-Book-Depth-Streams), [local order-book recovery](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/How-to-manage-a-local-order-book-correctly), and [routed WebSocket connections](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/Connect).
