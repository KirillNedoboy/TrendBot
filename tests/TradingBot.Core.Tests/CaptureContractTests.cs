using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class CaptureContractTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VersionOneEnvelopePreservesDepthCausalFieldsAndProvenance()
    {
        BookDelta depth = new("depth-1", new InstrumentId("BTCUSDT"),
            new UtcTimestamp(BaseTime),
            [new BookLevel(new Price(100m), new Quantity(2m))], []);
        CaptureProvenance provenance = new("binance.usdm", "btcusdt@depth@100ms",
            "session-1", "depth:BTCUSDT:1:2", "{\"e\":\"depthUpdate\"}",
            ["public", "depth"]);
        CaptureEnvelope envelope = CaptureEnvelope.FromMarketEvent("depth-stream", 17,
            depth, new UtcTimestamp(BaseTime.AddMilliseconds(1)),
            new UtcTimestamp(BaseTime.AddMilliseconds(2)), 5,
            new UtcTimestamp(BaseTime.AddMilliseconds(-1)), provenance);

        Assert.Equal(CaptureEnvelope.CurrentFormatVersion, envelope.FormatVersion);
        Assert.Equal(CaptureRecordKind.Depth, envelope.Kind);
        Assert.Equal(17, envelope.CaptureSequence);
        Assert.Equal(5, envelope.StableSequence);
        Assert.Equal(depth, envelope.Payload);
        Assert.Equal(provenance, envelope.Provenance);

        CaptureEnvelope restored = CoreJsonSerializer.Deserialize<CaptureEnvelope>(
            CoreJsonSerializer.Serialize(envelope));
        Assert.Equal(envelope, restored);
        Assert.Equal(envelope.CausalEvent, restored.CausalEvent);
    }

    [Fact]
    public void TradeAndDiagnosticAreTheOnlyOtherAllowedKinds()
    {
        Trade trade = new("trade-1", new InstrumentId("BTCUSDT"),
            new UtcTimestamp(BaseTime), new Price(100m), new Quantity(1m), TradeSide.Buy);
        CaptureEnvelope tradeEnvelope = CaptureEnvelope.FromMarketEvent("trade-stream", 0,
            trade, new UtcTimestamp(BaseTime), new UtcTimestamp(BaseTime), 0,
            new CaptureProvenance("binance.usdm", "btcusdt@aggTrade", "session-1",
                "aggTrade:BTCUSDT:1", "raw", []));
        CaptureDiagnostic diagnostic = new("gap", "A depth sequence gap was observed.",
            ["stream=btcusdt@depth"]);
        CaptureEnvelope diagnosticEnvelope = CaptureEnvelope.FromDiagnostic("diag-stream", 1,
            new UtcTimestamp(BaseTime.AddSeconds(1)),
            new UtcTimestamp(BaseTime.AddSeconds(2)), 1, diagnostic,
            new CaptureProvenance("binance.usdm", "btcusdt@depth", "session-1", null,
                null, ["gap"]));

        Assert.Equal(CaptureRecordKind.Trade, tradeEnvelope.Kind);
        Assert.Equal(CaptureRecordKind.Diagnostic, diagnosticEnvelope.Kind);
        Assert.Equal(diagnostic, CoreJsonSerializer.Deserialize<CaptureDiagnostic>(
            CoreJsonSerializer.Serialize(diagnostic)));
    }

    [Fact]
    public void EnvelopeRejectsBarsOrdersAndMutableOrInvalidInputs()
    {
        Bar bar = new("bar-1", new InstrumentId("BTCUSDT"), new UtcTimestamp(BaseTime),
            new UtcTimestamp(BaseTime), new UtcTimestamp(BaseTime.AddMinutes(1)),
            new Price(100m), new Price(101m), new Price(99m), new Price(100m),
            new Quantity(1m));
        CaptureProvenance provenance = new("binance.usdm", "btcusdt@kline_1m", "session-1",
            "bar-1", "raw", []);

        Assert.Throws<ArgumentException>(() => CaptureEnvelope.FromMarketEvent("stream", 0,
            bar, new UtcTimestamp(BaseTime), new UtcTimestamp(BaseTime), 0, provenance));
        Assert.Throws<ArgumentException>(() => new CaptureEnvelope(
            CaptureEnvelope.CurrentFormatVersion, "stream", 0, CaptureRecordKind.Depth,
            null, null, new UtcTimestamp(BaseTime), new UtcTimestamp(BaseTime),
            new UtcTimestamp(BaseTime), 0, null, provenance));
        Assert.Throws<ArgumentException>(() => CaptureEnvelope.FromDiagnostic("stream", 0,
            new UtcTimestamp(BaseTime.AddSeconds(1)), new UtcTimestamp(BaseTime),
            new UtcTimestamp(BaseTime.AddMilliseconds(-1)), 0,
            new CaptureDiagnostic("gap", "message", []), provenance));

        Assert.Empty(provenance.QualityFlags);
    }
}
