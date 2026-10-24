using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class LaneCQualityDecisionTests
{
    private static readonly DateTimeOffset AsOf =
        new(2026, 10, 24, 12, 0, 5, TimeSpan.Zero);

    [Fact]
    public void UnknownRequiredProviderQualityMapsToUnknownDecisionEvidence()
    {
        InstrumentId instrument = new("BTCUSDT");
        CausalEventCursor cursor = new(new UtcTimestamp(AsOf), 10);
        LaneCFeatureInput input = new(instrument, [], cursor, "quality-fixture");
        LaneCFeatureConfiguration configuration = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            1, 10);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);
        LaneCQualityPolicy policy = new(true, true, null);

        LaneCQualityEvidence quality = LaneCQualityEvaluator.Evaluate(input,
            configuration, features, policy);
        DecisionEvidence evidence = DecisionEvidence.FromLaneCQuality(quality,
            "quality-fixture");

        Assert.Equal(LaneCQualityState.InsufficientData, quality.State);
        Assert.Equal(cursor, quality.Cursor);
        Assert.Equal(input.InstrumentId, quality.InstrumentId);
        Assert.Equal(features.InputIdentity, quality.InputIdentity);
        Assert.Equal(DecisionEvidenceStatus.Unknown, evidence.Status);
        Assert.Equal(quality.EvidenceIdentity, evidence.EvidenceId);
        Assert.Equal(cursor, evidence.Cursor);
        Assert.NotNull(evidence.LaneCQualityMetadata);
        Assert.Equal(input.InstrumentId, evidence.LaneCQualityMetadata!.InstrumentId);
        Assert.Contains("provider-silence-policy-unknown", quality.Reasons);
        Assert.Equal(quality, CoreJsonSerializer.Deserialize<LaneCQualityEvidence>(
            CoreJsonSerializer.Serialize(quality)));
        Assert.Equal(evidence, CoreJsonSerializer.Deserialize<DecisionEvidence>(
            CoreJsonSerializer.Serialize(evidence)));
    }

    [Fact]
    public void UnavailableRequiredProviderFailsClosedAsInsufficientData()
    {
        InstrumentId instrument = new("BTCUSDT");
        CausalEventCursor cursor = new(new UtcTimestamp(AsOf), 10);
        LaneCFeatureInput input = new(instrument, [], cursor, "quality-fixture");
        LaneCFeatureConfiguration configuration = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            1, 10);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);
        LaneCQualityEvidence quality = LaneCQualityEvaluator.Evaluate(input,
            configuration, features, new LaneCQualityPolicy(true, true,
                TimeSpan.FromSeconds(2)));

        Assert.Equal(LaneCQualityState.InsufficientData, quality.State);
        Assert.Equal(DecisionEvidenceStatus.Unknown,
            DecisionEvidence.FromLaneCQuality(quality, "quality-fixture").Status);
        Assert.Contains("required-order-book-unavailable", quality.Reasons);
        Assert.Contains("required-trades-unavailable", quality.Reasons);
    }

    [Fact]
    public void FutureDiagnosticsDoNotAffectCausalQualityAndUnknownCodesKeepSafeProvenance()
    {
        InstrumentId instrument = new("BTCUSDT");
        CausalEventCursor cursor = new(new UtcTimestamp(AsOf), 10);
        CaptureEnvelope futureAvailability = CaptureEnvelope.FromDiagnostic("lane-c", 0,
            new UtcTimestamp(AsOf.AddSeconds(-1)), new UtcTimestamp(AsOf.AddSeconds(1)),
            new UtcTimestamp(AsOf.AddSeconds(1)), 9,
            new CaptureDiagnostic("future-availability-code", "private detail", []),
            new CaptureProvenance("binance-public", "depth", "session-1", null,
                "credential=hidden", []));
        CaptureEnvelope futureSequence = CaptureEnvelope.FromDiagnostic("lane-c", 1,
            new UtcTimestamp(AsOf.AddSeconds(-1)), new UtcTimestamp(AsOf.AddSeconds(-1)),
            11, new CaptureDiagnostic("future-sequence-code", "private detail", []),
            new CaptureProvenance("binance-public", "depth", "session-1", null,
                null, []));
        LaneCFeatureInput input = new(instrument, [futureAvailability, futureSequence], cursor,
            "quality-fixture");
        LaneCFeatureConfiguration configuration = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            1, 10);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);
        LaneCQualityEvidence quality = LaneCQualityEvaluator.Evaluate(input,
            configuration, features, new LaneCQualityPolicy(false, false, null));

        Assert.Empty(quality.UnknownDiagnostics);
        Assert.Equal(0, quality.SignalCounts.GapCount);

        CaptureEnvelope current = CaptureEnvelope.FromDiagnostic("lane-c", 0,
            new UtcTimestamp(AsOf), new UtcTimestamp(AsOf), 10,
            new CaptureDiagnostic("custom-code", "unexported message", []),
            new CaptureProvenance("binance-public", "depth", "session-1", null,
                "credential=hidden", []));
        LaneCFeatureInput currentInput = new(instrument, [current], cursor, "quality-fixture");
        LaneCFeatureResult currentFeatures = LaneCFeatureEvaluator.Evaluate(currentInput,
            configuration);
        LaneCQualityEvidence currentQuality = LaneCQualityEvaluator.Evaluate(currentInput,
            configuration, currentFeatures, new LaneCQualityPolicy(false, false, null));
        LaneCQualityUnknownDiagnostic unknown = Assert.Single(currentQuality.UnknownDiagnostics);
        Assert.Equal("custom-code", unknown.Code);
        Assert.Equal("binance-public", unknown.Source);
        Assert.Equal("depth", unknown.StreamName);
        Assert.Equal(10, unknown.StableSequence);
        Assert.DoesNotContain("private", CoreJsonSerializer.Serialize(currentQuality),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActiveRecoveryGapBecomesVetoEvidence()
    {
        CausalEventCursor cursor = new(new UtcTimestamp(AsOf), 10);
        CaptureEnvelope gap = CaptureEnvelope.FromDiagnostic("lane-c", 0,
            new UtcTimestamp(AsOf.AddSeconds(-1)), new UtcTimestamp(AsOf.AddSeconds(-1)), 10,
            new CaptureDiagnostic("BinanceDepthRecoveryGap", "gap", []),
            new CaptureProvenance("binance-public", "depth", "session-1", null,
                null, []));
        LaneCFeatureInput input = new(new InstrumentId("BTCUSDT"), [gap], cursor,
            "quality-fixture");
        LaneCFeatureConfiguration configuration = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            1, 10);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);
        LaneCQualityEvidence quality = LaneCQualityEvaluator.Evaluate(input,
            configuration, features, new LaneCQualityPolicy(false, false, null));

        Assert.Equal(LaneCQualityState.SequenceGap, quality.State);
        Assert.Equal(1, quality.SignalCounts.GapCount);
        Assert.Equal(DecisionEvidenceStatus.Veto,
            DecisionEvidence.FromLaneCQuality(quality, "quality-fixture").Status);
    }

    [Fact]
    public void StaleRequiredTradeProviderBecomesVetoEvidence()
    {
        DateTimeOffset tradeTime = AsOf.AddSeconds(-3);
        InstrumentId instrument = new("BTCUSDT");
        CaptureEnvelope trade = CaptureEnvelope.FromMarketEvent("lane-c", 0,
            new Trade("old-trade", instrument, new UtcTimestamp(tradeTime),
                new Price(100m), new Quantity(1m), TradeSide.Buy),
            new UtcTimestamp(tradeTime), new UtcTimestamp(tradeTime), 0,
            new CaptureProvenance("binance-public", "aggTrade", "session-1", null,
                null, []));
        CausalEventCursor cursor = new(new UtcTimestamp(AsOf), 0);
        LaneCFeatureInput input = new(instrument, [trade], cursor, "quality-fixture");
        LaneCFeatureConfiguration configuration = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            1, 10);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);
        LaneCQualityEvidence quality = LaneCQualityEvaluator.Evaluate(input,
            configuration, features, new LaneCQualityPolicy(false, true,
                TimeSpan.FromSeconds(1)));

        Assert.Equal(LaneCQualityState.Stale, quality.State);
        Assert.Contains("trade-provider-stale", quality.Reasons);
        Assert.Equal(DecisionEvidenceStatus.Veto,
            DecisionEvidence.FromLaneCQuality(quality, "quality-fixture").Status);
    }

    [Fact]
    public void RequiredUnknownQualityBlocksTheDecisionEngine()
    {
        SetupEvidenceSnapshot laneA = LifecycleInputFixtures.All[0].Evidence;
        LaneCFeatureInput input = new(laneA.InputSequence.InstrumentId, [], laneA.Cursor,
            "quality-fixture");
        LaneCFeatureConfiguration configuration = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            1, 10);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);
        LaneCQualityEvidence quality = LaneCQualityEvaluator.Evaluate(input,
            configuration, features, new LaneCQualityPolicy(true, true, null));
        DecisionEvidence evidence = DecisionEvidence.FromLaneCQuality(quality,
            "quality-fixture");

        DecisionEngineResult result = DecisionEngine.Evaluate(new DecisionEngineInput(
            "lane-c-quality-block", Lifecycle(laneA), laneA, [evidence], "quality-fixture"),
            new DecisionEngineConfiguration("rules-v1", "configuration-v1",
                DecisionEngineProfile.Allow, requireLaneB: false, requireLaneC: true,
                allowNeutralLaneB: true, allowNeutralLaneC: false));

        Assert.Equal(DecisionOutcome.Block, result.Decision.Outcome);
        Assert.Contains(DecisionReasonCodes.LaneCUnknown, result.Reasons);
        Assert.NotNull(result.Inputs.LaneEvidence.Single().LaneCQualityMetadata);
    }

    [Fact]
    public void RejectsQualityEvidenceForAnotherInstrument()
    {
        SetupEvidenceSnapshot laneA = LifecycleInputFixtures.All[0].Evidence;
        LaneCFeatureInput input = new(new InstrumentId("ETHUSDT"), [], laneA.Cursor,
            "quality-fixture");
        LaneCFeatureConfiguration configuration = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            1, 10);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);
        LaneCQualityEvidence quality = LaneCQualityEvaluator.Evaluate(input,
            configuration, features, new LaneCQualityPolicy(true, true, null));
        DecisionEvidence evidence = DecisionEvidence.FromLaneCQuality(quality,
            "quality-fixture");

        Assert.Throws<ArgumentException>(() => new DecisionEngineInput("wrong-instrument",
            Lifecycle(laneA), laneA, [evidence], "quality-fixture"));
    }

    private static SetupLifecycleSnapshot Lifecycle(SetupEvidenceSnapshot evidence)
    {
        SetupLifecycleIdentity identity = SetupLifecycleIdentity.Create(
            evidence.InputSequence.InstrumentId, evidence.SetupType, evidence.Direction,
            "lane-c-quality-anchor", evidence.Cursor.AsOf, "context-v1");
        return SetupLifecycleSnapshot.CreateInitial(identity,
            DecisionProjectionFixtures.CreateLifecycleConfiguration()) with
        {
            State = SetupState.EntryArmed,
            ProcessedCursor = evidence.Cursor,
            ProcessedAvailabilityTime = evidence.Cursor.AsOf
        };
    }
}
