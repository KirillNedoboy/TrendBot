using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class LaneBQualityTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void MissingFreshnessPolicyProducesUnknownQualityWithoutChangingShadowOutcome()
    {
        LaneBShadowResult shadow = EvaluateShadow(policy: null);

        Assert.Equal(LaneBShadowStatus.Confirm, shadow.Status);
        LaneBQualityState quality = LaneBQualityEvaluator.Evaluate(shadow);

        Assert.Equal(LaneBQualityStatus.Unknown, quality.Status);
        Assert.False(quality.IsReady);
        Assert.Contains(quality.Reasons,
            reason => reason.Contains("freshness", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingAndUnavailableInputStatesRemainDistinctAndFailClosed()
    {
        CausalEventCursor cursor = Cursor(2, 2);
        LaneBShadowResult missing = ShadowWithSelection(new LaneBInputSelection(
            LaneBInputStatus.MissingData, cursor, Empty<LaneBOpenInterestObservation>(),
            Empty<LaneBFundingObservation>(), null,
            new ImmutableValueList<string>(["missing-funding"])));
        LaneBShadowResult unavailable = ShadowWithSelection(new LaneBInputSelection(
            LaneBInputStatus.Unavailable, cursor, Empty<LaneBOpenInterestObservation>(),
            Empty<LaneBFundingObservation>(), null,
            new ImmutableValueList<string>(["unavailable-funding"])));

        Assert.Equal(LaneBQualityStatus.Missing,
            LaneBQualityEvaluator.Evaluate(missing).Status);
        Assert.Equal(LaneBQualityStatus.Unavailable,
            LaneBQualityEvaluator.Evaluate(unavailable).Status);
        Assert.False(LaneBQualityEvaluator.Evaluate(missing).IsReady);
        Assert.False(LaneBQualityEvaluator.Evaluate(unavailable).IsReady);
    }

    [Fact]
    public void StaleFreshnessConflictAndOverflowRemainSeparateQualityStates()
    {
        LaneBShadowResult staleShadow = EvaluateShadow(TimeSpan.FromSeconds(30));
        CausalEventCursor cursor = Cursor(2, 2);
        LaneBShadowResult conflict = ShadowWithSelection(new LaneBInputSelection(
            LaneBInputStatus.Conflict, cursor, Empty<LaneBOpenInterestObservation>(),
            Empty<LaneBFundingObservation>(), null,
            new ImmutableValueList<string>(["conflict-funding"])));
        LaneBShadowResult overflow = ShadowWithSelection(new LaneBInputSelection(
            LaneBInputStatus.ArithmeticOverflow, cursor, Empty<LaneBOpenInterestObservation>(),
            Empty<LaneBFundingObservation>(), null,
            new ImmutableValueList<string>(["open-interest-delta-overflow"])));

        Assert.Equal(LaneBQualityStatus.Stale,
            LaneBQualityEvaluator.Evaluate(staleShadow).Status);
        Assert.Equal(LaneBQualityStatus.Conflict,
            LaneBQualityEvaluator.Evaluate(conflict).Status);
        Assert.Equal(LaneBQualityStatus.ArithmeticOverflow,
            LaneBQualityEvaluator.Evaluate(overflow).Status);
    }

    [Fact]
    public void QualityRejectsCursorAndShadowSelectionCompatibilityDrift()
    {
        CausalEventCursor shadowCursor = Cursor(2, 2);
        LaneBInputSelection selection = new(LaneBInputStatus.Ready, Cursor(1, 1),
            Empty<LaneBOpenInterestObservation>(), Empty<LaneBFundingObservation>(), null,
            Empty<string>());
        LaneBShadowResult shadow = new(LaneBShadowResult.CurrentAlgorithmVersion, "rules-v1",
            "rule-1", SetupType.BoundaryRejection, Direction.Long, LaneBShadowStatus.Confirm,
            shadowCursor, selection, Empty<LaneBLiquidationObservation>(),
            Empty<LaneBFailedContinuationObservation>(), Empty<string>(), Empty<string>());

        LaneBQualityState quality = LaneBQualityEvaluator.Evaluate(shadow);

        Assert.Equal(LaneBQualityStatus.Incompatible, quality.Status);
        Assert.Contains(quality.Reasons,
            reason => reason.Contains("cursor", StringComparison.Ordinal));
    }

    [Fact]
    public void QualityStateRetainsCursorReferencesFreshnessAndStableReasonsInJson()
    {
        LaneBShadowResult shadow = EvaluateShadow(TimeSpan.FromMinutes(5));
        LaneBQualityState quality = LaneBQualityEvaluator.Evaluate(shadow);
        string json = CoreJsonSerializer.Serialize(quality);
        LaneBQualityState restored = CoreJsonSerializer.Deserialize<LaneBQualityState>(json);

        Assert.Equal(LaneBQualityStatus.Ready, quality.Status);
        Assert.Equal(quality, restored);
        Assert.Equal(quality.Cursor, restored.Cursor);
        Assert.Equal(quality.ObservationReferences, restored.ObservationReferences);
        Assert.Equal(quality.Freshness, restored.Freshness);
        Assert.Equal(quality.Reasons, restored.Reasons);
        Assert.DoesNotContain("confirm", json, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<LaneBQualityState>(
            json.TrimEnd('}') + ",\"future\":true}"));
    }

    private static LaneBShadowResult EvaluateShadow(TimeSpan? policy)
    {
        LaneBInput input = new(Instrument, [], [
            new LaneBFundingObservation("funding", new FundingUpdate("funding-1", Instrument,
                Utc(1), 0.01m), "rate", "periodic", "fixture", Utc(1), Utc(1), 1)
        ], "fixture");
        LaneBInputConfiguration inputConfiguration = new(
            "input-v1", null, "rate", "periodic", false, true, false);
        LaneBRuleTable rules = new("rules-v1", [
            new LaneBRule("rule-1", SetupType.BoundaryRejection, Direction.Long, "rule-v1", [
                new LaneBRuleCondition("funding", LaneBMetricKind.FundingRate,
                    LaneBComparisonOperator.GreaterThan, 0m),
                new LaneBRuleCondition("behavior", LaneBMetricKind.FailedContinuation,
                    LaneBComparisonOperator.Equal, 1m)
            ], LaneBShadowStatus.Confirm)
        ]);
        LaneBFailedContinuationObservation behavior = new(
            "behavior", "fc-1", Instrument, Utc(1), Utc(1), Utc(1), null, 2,
            "failed-continuation-v1", LaneBFailedContinuationOutcome.Observed,
            ["setup-1"], "fixture");
        LaneBFreshnessPolicy? freshness = policy is null
            ? null
            : new LaneBFreshnessPolicy(null, policy, null, policy);

        return freshness is null
            ? new LaneBEvaluator(rules).Evaluate(input, Cursor(2, 2), inputConfiguration,
                [], [behavior], SetupType.BoundaryRejection, Direction.Long)
            : new LaneBEvaluator(rules).Evaluate(input, Cursor(2, 2), inputConfiguration,
                [], [behavior], SetupType.BoundaryRejection, Direction.Long, freshness);
    }

    private static LaneBShadowResult ShadowWithSelection(LaneBInputSelection selection) =>
        new(LaneBShadowResult.CurrentAlgorithmVersion, "rules-v1", "rule-1",
            SetupType.BoundaryRejection, Direction.Long, LaneBShadowStatus.Confirm,
            selection.Cursor, selection, Empty<LaneBLiquidationObservation>(),
            Empty<LaneBFailedContinuationObservation>(), Empty<string>(), selection.Reasons);

    private static ImmutableValueList<T> Empty<T>() => new(Array.Empty<T>());

    private static CausalEventCursor Cursor(int offset, long sequence) =>
        new(Utc(offset), sequence);

    private static UtcTimestamp Utc(int offset) => new(BaseTime.AddMinutes(offset));
}
