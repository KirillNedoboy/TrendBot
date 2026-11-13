using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class SignalMultiLaneResearchTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset Cut = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly SignalMultiLanePolicy Policy = new(
        SignalMultiLanePolicy.CurrentAlgorithmVersion, TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(10),
        new LaneCFeatureConfiguration("lane-c-formulas-v1", TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), 1, 50));

    [Theory]
    [InlineData(Direction.Long, 101, 4, 1, DecisionEvidenceStatus.Confirm, DecisionEvidenceStatus.Confirm)]
    [InlineData(Direction.Long, 99, 1, 4, DecisionEvidenceStatus.Veto, DecisionEvidenceStatus.Veto)]
    [InlineData(Direction.Short, 99, 1, 4, DecisionEvidenceStatus.Confirm, DecisionEvidenceStatus.Confirm)]
    [InlineData(Direction.Short, 101, 4, 1, DecisionEvidenceStatus.Veto, DecisionEvidenceStatus.Veto)]
    public void BothLanesParticipateInDirectionalAdmission(Direction direction, decimal currentPrice,
        decimal bidQuantity, decimal askQuantity, DecisionEvidenceStatus expectedB,
        DecisionEvidenceStatus expectedC)
    {
        bool positiveFlow = expectedC == DecisionEvidenceStatus.Confirm
            ? direction == Direction.Long : direction == Direction.Short;
        SignalMultiLaneInput input = Input(direction, currentPrice, bidQuantity, askQuantity,
            positiveFlow ? TradeSide.Buy : TradeSide.Sell);
        SignalMultiLaneAssessment assessment = SignalMultiLaneResearchEvaluator.Evaluate(input);

        Assert.Equal(expectedB, assessment.LaneB);
        Assert.Equal(expectedC, assessment.LaneC);
        Assert.Equal(expectedB != DecisionEvidenceStatus.Veto && expectedC != DecisionEvidenceStatus.Veto,
            assessment.IsAllowed);
    }

    [Fact]
    public void FallingOpenInterestAndMixedFlowAreNeutralRatherThanConfirmation()
    {
        SignalMultiLaneInput input = Input(Direction.Long, 101m, 4m, 1m, TradeSide.Sell,
            currentInterest: 90m);
        SignalMultiLaneAssessment assessment = SignalMultiLaneResearchEvaluator.Evaluate(input);

        Assert.Equal(DecisionEvidenceStatus.Neutral, assessment.LaneB);
        Assert.Equal(DecisionEvidenceStatus.Neutral, assessment.LaneC);
        Assert.True(assessment.IsAllowed);
    }

    [Fact]
    public void MissingOrFutureDataAndIncompleteCoverageFailClosed()
    {
        SignalMultiLaneInput complete = Input(Direction.Long, 101m, 4m, 1m, TradeSide.Buy);
        SignalMultiLaneInput missing = new(Instrument, Direction.Long, complete.DecisionAt, Policy,
            null, complete.CurrentOpenInterest, complete.CurrentMarkPrice, null, null);
        SignalMultiLaneInput future = new(Instrument, Direction.Long, complete.DecisionAt, Policy,
            complete.PreviousOpenInterest, complete.CurrentOpenInterest,
            Mark("future", 101m, Cut.AddSeconds(1)), Utc(Cut.AddSeconds(-59)),
            complete.MicrostructureInput);

        SignalMultiLaneAssessment missingResult = SignalMultiLaneResearchEvaluator.Evaluate(missing);
        SignalMultiLaneAssessment futureResult = SignalMultiLaneResearchEvaluator.Evaluate(future);
        Assert.Equal(DecisionEvidenceStatus.Unknown, missingResult.LaneB);
        Assert.Equal(DecisionEvidenceStatus.Unknown, missingResult.LaneC);
        Assert.False(missingResult.IsAllowed);
        Assert.Equal(DecisionEvidenceStatus.Unknown, futureResult.LaneB);
        Assert.Equal(DecisionEvidenceStatus.Unknown, futureResult.LaneC);
        Assert.False(futureResult.IsAllowed);
    }

    [Fact]
    public void StaleInputsAndFundingForecastSignNeverCreateFalseConfirmation()
    {
        SignalMultiLaneInput complete = Input(Direction.Long, 101m, 4m, 1m, TradeSide.Buy);
        SignalMultiLaneInput stale = new(Instrument, Direction.Long, complete.DecisionAt, Policy,
            complete.PreviousOpenInterest, complete.CurrentOpenInterest,
            Mark("stale", 101m, Cut.AddSeconds(-11)), complete.TradeCoverageStart,
            complete.MicrostructureInput);
        SignalMultiLaneInput oppositeFunding = new(Instrument, Direction.Long, complete.DecisionAt,
            Policy, complete.PreviousOpenInterest, complete.CurrentOpenInterest,
            Mark("negative-funding", 101m, Cut.AddSeconds(-1), -0.01m),
            complete.TradeCoverageStart, complete.MicrostructureInput);

        Assert.Equal(DecisionEvidenceStatus.Unknown,
            SignalMultiLaneResearchEvaluator.Evaluate(stale).LaneB);
        Assert.Equal(DecisionEvidenceStatus.Confirm,
            SignalMultiLaneResearchEvaluator.Evaluate(oppositeFunding).LaneB);
    }

    [Fact]
    public void RecentlyReceivedOldTradeDoesNotPassMicrostructureFreshness()
    {
        SignalMultiLaneInput complete = Input(Direction.Long, 101m, 4m, 1m, TradeSide.Buy);
        CaptureEnvelope[] records =
        [
            complete.MicrostructureInput!.Records[0],
            complete.MicrostructureInput.Records[1],
            CaptureEnvelope.FromMarketEvent("fixture", 2,
                new Trade("delayed", Instrument, Utc(Cut.AddSeconds(-11)),
                    new Price(100m), new Quantity(1m), TradeSide.Buy),
                Utc(Cut), Utc(Cut), 2,
                new CaptureProvenance("fixture", "trades", "session", "delayed", null,
                    new ImmutableValueList<string>([]), null, 2))
        ];
        SignalMultiLaneInput delayed = new(Instrument, Direction.Long, complete.DecisionAt, Policy,
            complete.PreviousOpenInterest, complete.CurrentOpenInterest, complete.CurrentMarkPrice,
            complete.TradeCoverageStart, new LaneCFeatureInput(Instrument, records,
                new CausalEventCursor(Utc(Cut), 2), "fixture"));

        SignalMultiLaneAssessment result = SignalMultiLaneResearchEvaluator.Evaluate(delayed);
        Assert.Equal(DecisionEvidenceStatus.Unknown, result.LaneC);
        Assert.False(result.IsAllowed);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void IncompleteDepthCannotBecomeNeutralOrConfirm(decimal bid, decimal ask)
    {
        SignalMultiLaneInput complete = Input(Direction.Long, 101m, 4m, 1m, TradeSide.Buy);
        CaptureEnvelope[] records =
        [
            complete.MicrostructureInput!.Records[0],
            Book(1, "incomplete", 101, bid, ask, Cut.AddSeconds(-1)),
            complete.MicrostructureInput.Records[2]
        ];
        SignalMultiLaneInput incomplete = new(Instrument, Direction.Long, complete.DecisionAt,
            Policy, complete.PreviousOpenInterest, complete.CurrentOpenInterest,
            complete.CurrentMarkPrice, complete.TradeCoverageStart,
            new LaneCFeatureInput(Instrument, records, new CausalEventCursor(Utc(Cut), 2), "fixture"));
        Assert.Equal(DecisionEvidenceStatus.Unknown,
            SignalMultiLaneResearchEvaluator.Evaluate(incomplete).LaneC);
    }

    [Fact]
    public void RoundTripRetainsInputsAndReproducesAssessment()
    {
        SignalMultiLaneInput input = Input(Direction.Long, 101m, 4m, 1m, TradeSide.Buy);
        SignalMultiLaneInput restored = CoreJsonSerializer.Deserialize<SignalMultiLaneInput>(
            CoreJsonSerializer.Serialize(input));

        Assert.Equal(input, restored);
        Assert.Equal(SignalMultiLaneResearchEvaluator.Evaluate(input),
            SignalMultiLaneResearchEvaluator.Evaluate(restored));
    }

    [Fact]
    public void CommittedSignalEvidenceBindsBothLanesAndRejectsVetoOrTampering()
    {
        SignalMultiLaneInput input = Input(Direction.Long, 101m, 4m, 1m, TradeSide.Buy);
        SignalIdentity identity = SignalIdentity.Create(Instrument, "BINANCE_USDM", "15m",
            Direction.Long, SetupType.BoundaryRejection, "research-v1", "boundary-v1", "setup");
        UtcTimestamp setupTime = Utc(Cut.AddSeconds(-2));
        UtcTimestamp contextTime = Utc(Cut.AddHours(-1));
        SignalProvenance provenance = SignalProvenance.Create("research-v1", new string('a', 64),
            new CausalEventCursor(setupTime, 1), new CausalEventCursor(contextTime, 0),
            [new SignalInputReference("public", "v1", "setup", new string('b', 64),
                SignalInputRole.Setup, "15m", setupTime, setupTime, 1),
             new SignalInputReference("public", "v1", "context", new string('c', 64),
                SignalInputRole.HigherTimeframe, "1h", contextTime, contextTime, 0)]);
        SignalEvidenceRecord evidence = SignalEvidenceRecord.Create(identity, provenance,
            SignalEvidenceState.Complete, [], input);
        SignalEvidenceRecord restored = CoreJsonSerializer.Deserialize<SignalEvidenceRecord>(
            CoreJsonSerializer.Serialize(evidence));
        Assert.Equal(SignalEvidenceRecord.MultiLaneFormatVersion, evidence.FormatVersion);
        Assert.Equal(evidence, restored);
        restored.ValidateFor(identity);

        SignalMultiLaneInput veto = Input(Direction.Long, 99m, 4m, 1m, TradeSide.Buy);
        Assert.Throws<ArgumentException>(() => SignalEvidenceRecord.Create(identity, provenance,
            SignalEvidenceState.Complete, [], veto));
        JsonObject tampered = JsonNode.Parse(CoreJsonSerializer.Serialize(evidence))!.AsObject();
        tampered["multiLaneInput"]!["currentMarkPrice"]!["fundingForecast"] = -123m;
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<SignalEvidenceRecord>(tampered.ToJsonString()));
    }

    private static SignalMultiLaneInput Input(Direction direction, decimal price,
        decimal bidQuantity, decimal askQuantity, TradeSide tradeSide,
        decimal currentInterest = 110m)
    {
        SignalMarkPriceObservation previousMark = Mark("mark-before", 100m, Cut.AddSeconds(-61));
        SignalMarkPriceObservation currentMark = Mark("mark-now", price, Cut.AddSeconds(-1));
        SignalOpenInterestMarkSample previous = new(Oi("oi-before", 100m, Cut.AddSeconds(-60), 1),
            previousMark);
        SignalOpenInterestMarkSample current = new(Oi("oi-now", currentInterest, Cut.AddSeconds(-1), 2),
            currentMark);
        CaptureEnvelope[] records =
        [
            Book(0, "book-before", 100, bidQuantity, askQuantity, Cut.AddSeconds(-2)),
            Book(1, "book-now", 101, bidQuantity, askQuantity, Cut.AddSeconds(-1)),
            Trade(2, tradeSide, Cut.AddSeconds(-1))
        ];
        LaneCFeatureInput history = new(Instrument, records,
            new CausalEventCursor(Utc(Cut), 2), "fixture");
        return new SignalMultiLaneInput(Instrument, direction, Utc(Cut), Policy, previous,
            current, currentMark, Utc(Cut.AddSeconds(-60)), history);
    }

    private static LaneBOpenInterestObservation Oi(string id, decimal value,
        DateTimeOffset time, long sequence) => new("fixture-oi", new OIUpdate(id, Instrument,
            Utc(time), value), "exchange-reported-quantity", "fixture", Utc(time), Utc(time), sequence);

    private static SignalMarkPriceObservation Mark(string id, decimal price, DateTimeOffset time,
        decimal funding = 0.01m) => new("fixture-mark", "session", id, Instrument,
            new Price(price), funding, Utc(time), Utc(time), Utc(time), 1);

    private static CaptureEnvelope Book(long sequence, string id, long updateId,
        decimal bid, decimal ask, DateTimeOffset time) => CaptureEnvelope.FromMarketEvent("fixture",
        sequence, new BookDelta(id, Instrument, Utc(time),
            [new BookLevel(new Price(99m), new Quantity(bid))],
            [new BookLevel(new Price(101m), new Quantity(ask))]), Utc(time), Utc(time), sequence,
        new CaptureProvenance("fixture", "depth", "session", id, null,
            new ImmutableValueList<string>([]), new CaptureDepthMetadata(updateId, updateId,
                null, 1, true), sequence));

    private static CaptureEnvelope Trade(long sequence, TradeSide side, DateTimeOffset time) =>
        CaptureEnvelope.FromMarketEvent("fixture", sequence,
            new Trade("trade", Instrument, Utc(time), new Price(100m), new Quantity(1m), side),
            Utc(time), Utc(time), sequence,
            new CaptureProvenance("fixture", "trades", "session", "trade", null,
                new ImmutableValueList<string>([]), null, sequence));

    private static UtcTimestamp Utc(DateTimeOffset time) => new(time);
}
