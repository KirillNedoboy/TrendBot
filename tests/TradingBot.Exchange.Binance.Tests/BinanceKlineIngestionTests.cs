using TradingBot.Core;
using TradingBot.Exchange.Binance;
using Xunit;

namespace TradingBot.Exchange.Binance.Tests;

public sealed class BinanceKlineIngestionTests
{
    [Fact]
    public void SameOpenAtDifferentTimeframesProducesSeparateAcceptedBars()
    {
        BinanceKlineIngestionResult fifteenMinute = BinanceKlineIngestionBoundary.Evaluate(
            "BTCUSDT", Kline("15m", 0, 899_999), RestSource);
        BinanceKlineIngestionResult oneHour = BinanceKlineIngestionBoundary.Evaluate(
            "BTCUSDT", Kline("1h", 0, 3_599_999), WebSocketSource("btcusdt@kline_1h"));

        Assert.Equal(BinanceKlineIngestionStatus.Accepted, fifteenMinute.Status);
        Assert.Equal(BinanceKlineIngestionStatus.Accepted, oneHour.Status);
        Assert.Equal(2, fifteenMinute.Identity.FormatVersion);
        Assert.Equal("BINANCE", fifteenMinute.Identity.Venue);
        Assert.Equal("USD-M-PERPETUAL", fifteenMinute.Identity.Product);
        Assert.Equal("15m", fifteenMinute.Identity.Timeframe);
        Assert.Equal("1h", oneHour.Identity.Timeframe);
        Assert.Equal(0, fifteenMinute.Identity.OpenTimeMilliseconds);
        Assert.NotEqual(fifteenMinute.Identity, oneHour.Identity);
        Assert.NotEqual(fifteenMinute.CoreBar!.EventId, oneHour.CoreBar!.EventId);
    }

    [Fact]
    public void UtcGridAndInclusiveCloseMapToHalfOpenEndExclusiveBoundary()
    {
        const long openTime = 1_700_000_100_000;
        const long inclusiveCloseTime = openTime + 900_000 - 1;
        BinanceKline kline = Kline("15m", openTime, inclusiveCloseTime);

        BinanceKlineIngestionResult result = BinanceKlineIngestionBoundary.Evaluate(
            "BTCUSDT", kline, RestSource);

        Assert.Equal(BinanceKlineIngestionStatus.Accepted, result.Status);
        Assert.Equal(inclusiveCloseTime, kline.CloseTimeMilliseconds);
        Assert.False(kline.IsClosedAt(
            DateTimeOffset.FromUnixTimeMilliseconds(inclusiveCloseTime - 1)));
        Assert.True(kline.IsClosedAt(
            DateTimeOffset.FromUnixTimeMilliseconds(inclusiveCloseTime)));
        Assert.Equal(inclusiveCloseTime, result.InclusiveCloseTime!.Value
            .ToUnixTimeMilliseconds());
        Assert.Equal(openTime + 900_000, result.EndExclusiveTime!.Value
            .ToUnixTimeMilliseconds());
        Assert.Equal(result.EndExclusiveTime, result.CoreBar!.CloseTime);
        Assert.Equal(result.EndExclusiveTime, result.CoreBar.EventTime);
        Assert.Equal(openTime, result.CoreBar.OpenTime.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void RestAndWebSocketRepresentationsDeduplicateByAcceptedRevisionHash()
    {
        BinanceKline kline = Kline("15m", 1_700_000_100_000, 1_700_000_999_999);
        BinanceKlineIngestionResult rest = BinanceKlineIngestionBoundary.Evaluate(
            "BTCUSDT", kline, RestSource);

        BinanceKlineIngestionResult websocket = BinanceKlineIngestionBoundary.Evaluate(
            "BTCUSDT", kline, WebSocketSource("btcusdt@kline_15m"), rest.AcceptedRevision);

        Assert.Equal(BinanceKlineIngestionStatus.Accepted, rest.Status);
        Assert.Equal(BinanceKlineIngestionStatus.Duplicate, websocket.Status);
        Assert.Equal(rest.RevisionHash, websocket.RevisionHash);
        Assert.Null(websocket.CoreBar);
        Assert.Collection(websocket.AcceptedRevision!.SourceMetadata,
            source => Assert.Equal(BinanceKlineSourceKind.Rest, source.Kind),
            source => Assert.Equal(BinanceKlineSourceKind.WebSocket, source.Kind));
    }

    [Fact]
    public void DifferentClosedPayloadForSameIdentityIsAConflictAndCannotReplaceAcceptedRevision()
    {
        const long openTime = 1_700_000_100_000;
        const long closeTime = openTime + 900_000 - 1;
        BinanceKlineIngestionResult accepted = BinanceKlineIngestionBoundary.Evaluate(
            "BTCUSDT", Kline("15m", openTime, closeTime, close: 100m), RestSource);
        BinanceKline corrected = Kline("15m", openTime, closeTime, close: 100.5m);

        BinanceKlineIngestionResult conflict = BinanceKlineIngestionBoundary.Evaluate(
            "BTCUSDT", corrected, WebSocketSource("btcusdt@kline_15m"),
            accepted.AcceptedRevision);

        Assert.Equal(BinanceKlineIngestionStatus.RevisionConflict, conflict.Status);
        Assert.Equal("revision-conflict", conflict.Diagnostic!.Code);
        Assert.Null(conflict.CoreBar);
        Assert.Equal(accepted.RevisionHash, conflict.AcceptedRevision!.RevisionHash);
        Assert.NotEqual(accepted.RevisionHash, conflict.RevisionHash);
        Assert.Equal(100m, conflict.AcceptedRevision.CoreBar.Close.Value);
    }

    [Fact]
    public void OffGridOpenProducesDiagnosticAndNoCoreBar()
    {
        BinanceKlineIngestionResult result = BinanceKlineIngestionBoundary.Evaluate(
            "BTCUSDT", Kline("15m", 1, 900_000), RestSource);

        Assert.Equal(BinanceKlineIngestionStatus.Invalid, result.Status);
        Assert.Equal("open-time-off-utc-grid", result.Diagnostic!.Code);
        Assert.Null(result.CoreBar);
    }

    [Fact]
    public void IncorrectInclusiveCloseDurationProducesDiagnosticAndNoCoreBar()
    {
        BinanceKlineIngestionResult result = BinanceKlineIngestionBoundary.Evaluate(
            "BTCUSDT", Kline("15m", 0, 899_998), RestSource);

        Assert.Equal(BinanceKlineIngestionStatus.Invalid, result.Status);
        Assert.Equal("interval-duration-mismatch", result.Diagnostic!.Code);
        Assert.Null(result.CoreBar);
    }

    [Fact]
    public void NegativeOpenProducesDiagnosticAndNoCoreBar()
    {
        BinanceKlineIngestionResult result = BinanceKlineIngestionBoundary.Evaluate(
            "BTCUSDT", Kline("15m", -900_000, -1), RestSource);

        Assert.Equal(BinanceKlineIngestionStatus.Invalid, result.Status);
        Assert.Equal("invalid-open-time", result.Diagnostic!.Code);
        Assert.Null(result.CoreBar);
    }

    private static BinanceKline Kline(string timeframe, long openTime, long inclusiveCloseTime,
        decimal close = 100m, bool isClosed = true) => new(openTime, 100m,
        Math.Max(101m, close), 99m, close, 1m, inclusiveCloseTime, 100m, 1, 0.5m, 50m,
        "0", timeframe, isClosed);

    private static BinanceKlineSourceMetadata RestSource => new(
        BinanceKlineSourceKind.Rest, "GET /fapi/v1/klines", null, null, null, null);

    private static BinanceKlineSourceMetadata WebSocketSource(string streamName) => new(
        BinanceKlineSourceKind.WebSocket, streamName, 1_700_000_100_001,
        new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_100_002)),
        new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_100_003)), 1);
}
