using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class RiskEngineProtectionTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly PortfolioId Portfolio = new("portfolio-a");

    [Fact]
    public void MatchingFreshProtectionAndClearCooldownCanCompleteIndependentApproval()
    {
        RiskEvaluationInput input = CreateInput();

        RiskEvaluationResult result = RiskEvaluationEngine.Evaluate(
            input, CreateConfiguration(), new LinearPerpetualRiskMathCalculator());

        Assert.True(result.ProtectionPassed, string.Join(",", result.Reasons));
        Assert.True(result.IsApproved, string.Join(",", result.Reasons));
        Assert.Equal(RiskDecisionOutcome.Approved, result.RiskDecision.Outcome);
        Assert.Contains(RiskReasonCodes.Approved, result.Reasons);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("wrong-instrument")]
    [InlineData("wrong-direction")]
    [InlineData("wrong-setup")]
    [InlineData("stop-mismatch")]
    [InlineData("wrong-profit-side")]
    public void InvalidProtectiveEvidenceBlocksWithoutChangingSharedSizing(string failure)
    {
        DecisionProjectionSnapshot projection = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked: false);
        CausalEventCursor cursor = projection.CausalCursor;
        RiskProtectionEvidence protection = CreateProtection(projection, cursor);
        protection = failure switch
        {
            "stale" => CreateProtection(projection, cursor, freshness: RiskEvidenceFreshness.Stale),
            "future" => CreateProtection(projection,
                new CausalEventCursor(new UtcTimestamp(cursor.AsOf.Value.AddMinutes(1)),
                    cursor.MaxStableSequence + 1)),
            "wrong-instrument" => new RiskProtectionEvidence(new InstrumentId("ETHUSDT"),
                Direction.Long, projection.Result.Decision.SetupId, "method-v1", "source-v1",
                "protective-v1", RiskEvidenceFreshness.Fresh, cursor,
                new Price(90m), new Price(110m), new Price(120m), "fixture"),
            "wrong-direction" => new RiskProtectionEvidence(Instrument, Direction.Short,
                projection.Result.Decision.SetupId, "method-v1", "source-v1", "protective-v1",
                RiskEvidenceFreshness.Fresh, cursor, new Price(90m), new Price(110m),
                new Price(120m), "fixture"),
            "wrong-setup" => new RiskProtectionEvidence(Instrument, Direction.Long,
                "different-setup", "method-v1", "source-v1", "protective-v1",
                RiskEvidenceFreshness.Fresh, cursor, new Price(90m), new Price(110m),
                new Price(120m), "fixture"),
            "stop-mismatch" => new RiskProtectionEvidence(Instrument, Direction.Long,
                projection.Result.Decision.SetupId, "method-v1", "source-v1", "protective-v1",
                RiskEvidenceFreshness.Fresh, cursor, new Price(89m), new Price(110m),
                new Price(120m), "fixture"),
            "wrong-profit-side" => new RiskProtectionEvidence(Instrument, Direction.Long,
                projection.Result.Decision.SetupId, "method-v1", "source-v1", "protective-v1",
                RiskEvidenceFreshness.Fresh, cursor, new Price(90m), new Price(99m),
                new Price(120m), "fixture"),
            _ => protection
        };

        RiskEvaluationInput input = CreateInput(projection, protection, cursor);
        RiskEvaluationResult result = RiskEvaluationEngine.Evaluate(
            input, CreateConfiguration(), new LinearPerpetualRiskMathCalculator());

        Assert.False(result.ProtectionPassed);
        Assert.False(result.IsApproved);
        Assert.Equal(1m, result.RiskMathResult!.RawQuantity.Value);
        Assert.Contains(result.Reasons, reason => reason.StartsWith(
            "risk-protective-", StringComparison.Ordinal)
            || reason == RiskReasonCodes.EvidenceStale
            || reason == RiskReasonCodes.EvidenceFuture);
    }

    [Fact]
    public void ActiveCooldownBlocksBeforeExpiryAndClearAtTheBoundaryPermits()
    {
        DecisionProjectionSnapshot projection = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked: false);
        CausalEventCursor cursor = projection.CausalCursor;
        RiskProtectionEvidence protection = CreateProtection(projection, cursor);
        UtcTimestamp expiry = new(cursor.AsOf.Value.AddMinutes(5));

        RiskEvaluationResult active = RiskEvaluationEngine.Evaluate(
            CreateInput(projection, protection, cursor,
                RiskCooldownState.Active(Instrument, expiry, "cooldown-source", "cooldown-v1",
                    cursor, "fixture")),
            CreateConfiguration(), new LinearPerpetualRiskMathCalculator());
        Assert.False(active.IsApproved);
        Assert.Contains(RiskReasonCodes.CooldownActive, active.Reasons);

        CausalEventCursor atExpiry = cursor;
        RiskProtectionEvidence boundaryProtection = CreateProtection(projection, atExpiry);
        RiskEvaluationResult activeAtExpiry = RiskEvaluationEngine.Evaluate(
            CreateInput(projection, boundaryProtection, atExpiry,
                RiskCooldownState.Active(Instrument, atExpiry.AsOf, "cooldown-source",
                    "cooldown-v1", atExpiry, "fixture")),
            CreateConfiguration(), new LinearPerpetualRiskMathCalculator());
        Assert.True(activeAtExpiry.IsApproved, string.Join(",", activeAtExpiry.Reasons));

        RiskEvaluationResult clear = RiskEvaluationEngine.Evaluate(
            CreateInput(projection, boundaryProtection, atExpiry,
                RiskCooldownState.Clear(Instrument, "cooldown-source", "cooldown-v1",
                    atExpiry, "fixture")),
            CreateConfiguration(), new LinearPerpetualRiskMathCalculator());
        Assert.True(clear.IsApproved, string.Join(",", clear.Reasons));
    }

    [Fact]
    public void ProtectionAggregateRejectsLevelSourceOrMethodLinkConflict()
    {
        DecisionProjectionSnapshot projection = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked: false);
        CausalEventCursor cursor = projection.CausalCursor;
        string setupId = projection.Result.Decision.SetupId;
        RiskProtectionLevelEvidence stop = new(
            RiskProtectionLevelKind.ProtectiveStop, new Price(90m), Instrument,
            Direction.Long, setupId, "different-method", "source-v1", "protective-v1",
            RiskEvidenceFreshness.Fresh, cursor, "fixture");
        RiskProtectionLevelEvidence tp1 = new(
            RiskProtectionLevelKind.TakeProfit1, new Price(110m), Instrument, Direction.Long,
            setupId, "method-v1", "source-v1", "protective-v1", RiskEvidenceFreshness.Fresh,
            cursor, "fixture");
        RiskProtectionLevelEvidence tp2 = new(
            RiskProtectionLevelKind.TakeProfit2, new Price(120m), Instrument, Direction.Long,
            setupId, "method-v1", "source-v1", "protective-v1", RiskEvidenceFreshness.Fresh,
            cursor, "fixture");

        Assert.Throws<ArgumentException>(() => new RiskProtectionEvidence(Instrument,
            Direction.Long, setupId, "method-v1", "source-v1", "protective-v1",
            RiskEvidenceFreshness.Fresh, cursor, stop, tp1, tp2, "fixture"));
    }

    private static RiskEvaluationInput CreateInput(
        DecisionProjectionSnapshot? projection = null,
        RiskProtectionEvidence? protection = null,
        CausalEventCursor? cursor = null,
        RiskCooldownState? cooldown = null)
    {
        projection ??= DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked: false);
        cursor ??= projection.CausalCursor;
        protection ??= CreateProtection(projection, cursor);
        RiskMathInput math = new(Instrument, Direction.Long, new Money(1_000m, "USDT"),
            0.01m, new Price(100m), new Price(90m),
            new MoneyPerQuantityUnit(0m, "USDT"),
            new MoneyPerQuantityUnit(0m, "USDT"));
        return new RiskEvaluationInput("risk-protection", projection, math, Portfolio, cursor,
            new RiskPortfolioUsage(Portfolio, new Money(0m, "USDT"),
                new Money(0m, "USDT"), new Money(0m, "USDT"), 0, "usage-source", "usage-v1",
                cursor, "fixture"),
            new Money(0m, "USDT"), "period-2026-01-01", new Money(10m, "USDT"), 1,
            RiskHealthEvidence.Healthy("account-source", "account-v1", cursor, "fixture"),
            RiskHealthEvidence.Healthy("portfolio-source", "portfolio-v1", cursor, "fixture"),
            RiskHealthEvidence.Healthy("market-source", "market-v1", cursor, "fixture"),
            RiskKillSwitchEvidence.Clear("kill-source", "kill-v1", cursor, "fixture"),
            "fixture", protection, cooldown ?? RiskCooldownState.Clear(Instrument,
                "cooldown-source", "cooldown-v1", cursor, "fixture"));
    }

    private static RiskProtectionEvidence CreateProtection(DecisionProjectionSnapshot projection,
        CausalEventCursor cursor, RiskEvidenceFreshness freshness = RiskEvidenceFreshness.Fresh) =>
        new(Instrument, Direction.Long, projection.Result.Decision.SetupId, "method-v1",
            "source-v1", "protective-v1", freshness, cursor, new Price(90m), new Price(110m),
            new Price(120m), "fixture");

    private static RiskEvaluationConfiguration CreateConfiguration() => new(
        RiskEvaluationVersion.Current, new RiskMathVersion("linear-perpetual-risk-v1"),
        new RiskLimitConfiguration(new Money(10m, "USDT"), new Money(100m, "USDT"),
            new Money(100m, "USDT"), "period-2026-01-01"),
        new PortfolioLimits(Portfolio, new Money(100m, "USDT"),
            new Money(10_000m, "USDT"), 2), "fixture");
}
