using System.Reflection;
using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class RiskEngineFailClosedTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly PortfolioId Portfolio = new("portfolio-a");

    [Fact]
    public void EnabledKillSwitchBlocksBeforeCalculatorAndUnknownOperationalStateBlocks()
    {
        DecisionProjectionSnapshot projection = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked: false);
        CausalEventCursor cursor = projection.CausalCursor;
        RiskEvaluationInput enabled = CreateInput(projection, cursor,
            killSwitch: RiskKillSwitchEvidence.Enabled("kill-source", "kill-v1", cursor,
                "fixture"), calculatorMustBeCalled: false);

        CountingCalculator calculator = new(shouldThrow: true);
        RiskEvaluationResult killResult = RiskEvaluationEngine.Evaluate(
            enabled, CreateConfiguration(), calculator);
        Assert.False(killResult.IsApproved);
        Assert.Contains(RiskReasonCodes.KillSwitchEnabled, killResult.Reasons);
        Assert.Equal(0, calculator.Calls);

        RiskEvaluationInput unknownHealth = CreateInput(projection, cursor,
            accountHealth: RiskHealthEvidence.Unknown("account-source", "account-v1",
                cursor, "fixture"));
        RiskEvaluationResult healthResult = RiskEvaluationEngine.Evaluate(
            unknownHealth, CreateConfiguration(), new CountingCalculator());
        Assert.False(healthResult.IsApproved);
        Assert.Contains(RiskReasonCodes.AccountStateUnknown, healthResult.Reasons);
    }

    [Fact]
    public void MissingPoliciesAndCurrencyMismatchFailClosed()
    {
        RiskEvaluationInput input = CreateInput();
        RiskEvaluationConfiguration missing = new(RiskEvaluationVersion.Current,
            new RiskMathVersion("linear-perpetual-risk-v1"), null, null, "fixture");
        RiskEvaluationResult missingResult = RiskEvaluationEngine.Evaluate(
            input, missing, new LinearPerpetualRiskMathCalculator());
        Assert.False(missingResult.IsApproved);
        Assert.Contains(RiskReasonCodes.LimitsMissing, missingResult.Reasons);
        Assert.Contains(RiskReasonCodes.PortfolioLimitsMissing, missingResult.Reasons);

        RiskEvaluationInput wrongCurrency = CreateInput(candidateMargin: new Money(10m, "USD"));
        RiskEvaluationResult currencyResult = RiskEvaluationEngine.Evaluate(
            wrongCurrency, CreateConfiguration(), new LinearPerpetualRiskMathCalculator());
        Assert.False(currencyResult.IsApproved);
        Assert.Contains(RiskReasonCodes.LimitCurrencyMismatch, currencyResult.Reasons);
    }

    [Fact]
    public void FutureConflictingAndMismatchedEvidenceCannotCrossTheCausalCursor()
    {
        DecisionProjectionSnapshot projection = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked: false);
        CausalEventCursor cursor = projection.CausalCursor;

        RiskEvaluationInput future = CreateInput(projection, cursor,
            marketHealth: RiskHealthEvidence.Healthy("market-source", "market-v1",
                new CausalEventCursor(new UtcTimestamp(cursor.AsOf.Value.AddMinutes(1)),
                    cursor.MaxStableSequence + 1), "fixture"));
        RiskEvaluationResult futureResult = RiskEvaluationEngine.Evaluate(
            future, CreateConfiguration(), new LinearPerpetualRiskMathCalculator());
        Assert.Contains(RiskReasonCodes.EvidenceFuture, futureResult.Reasons);

        RiskEvaluationInput conflicting = CreateInput(projection, cursor,
            portfolioHealth: new RiskHealthEvidence("portfolio-source", "portfolio-v1",
                RiskHealthState.Conflicting, RiskEvidenceFreshness.Fresh, cursor, "fixture"));
        RiskEvaluationResult conflictResult = RiskEvaluationEngine.Evaluate(
            conflicting, CreateConfiguration(), new LinearPerpetualRiskMathCalculator());
        Assert.Contains(RiskReasonCodes.EvidenceConflict, conflictResult.Reasons);

        RiskEvaluationInput stale = CreateInput(projection, cursor,
            accountHealth: new RiskHealthEvidence("account-source", "account-v1",
                RiskHealthState.Healthy, RiskEvidenceFreshness.Stale, cursor, "fixture"));
        RiskEvaluationResult staleResult = RiskEvaluationEngine.Evaluate(
            stale, CreateConfiguration(), new LinearPerpetualRiskMathCalculator());
        Assert.Contains(RiskReasonCodes.EvidenceStale, staleResult.Reasons);
    }

    [Fact]
    public void LongAndShortUseTheirOwnLossAndProfitSides()
    {
        DecisionProjectionSnapshot projection = CreateShortProjection();
        CausalEventCursor cursor = projection.CausalCursor;
        RiskMathInput math = new(Instrument, Direction.Short, new Money(1_000m, "USDT"),
            0.01m, new Price(100m), new Price(110m),
            new MoneyPerQuantityUnit(0m, "USDT"),
            new MoneyPerQuantityUnit(0m, "USDT"));
        RiskProtectionEvidence protection = new(Instrument, Direction.Short,
            projection.Result.Decision.SetupId, "method-v1", "source-v1", "protective-v1",
            RiskEvidenceFreshness.Fresh, cursor, new Price(110m), new Price(90m),
            new Price(80m), "fixture");
        RiskEvaluationInput input = CreateInput(projection, cursor, math, protection);

        RiskEvaluationResult result = RiskEvaluationEngine.Evaluate(
            input, CreateConfiguration(), new LinearPerpetualRiskMathCalculator());

        Assert.True(result.IsApproved, string.Join(",", result.Reasons));
        Assert.Equal(Direction.Short, result.RiskMathResult!.Input.Direction);
        Assert.Equal(1m, result.RiskMathResult.RawQuantity.Value);
    }

    [Fact]
    public void CapacityOverflowBlocksAndRiskResultRetainsNoExecutionSurface()
    {
        DecisionProjectionSnapshot projection = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked: false);
        CausalEventCursor cursor = projection.CausalCursor;
        RiskEvaluationInput input = CreateInput(projection, cursor,
            portfolioUsage: new RiskPortfolioUsage(Portfolio,
                new Money(decimal.MaxValue, "USDT"), new Money(0m, "USDT"),
                new Money(0m, "USDT"), 0, "usage-source", "usage-v1", cursor, "fixture"));
        RiskEvaluationResult result = RiskEvaluationEngine.Evaluate(
            input, CreateConfiguration(), new LinearPerpetualRiskMathCalculator());

        Assert.False(result.IsApproved);
        Assert.Contains(RiskReasonCodes.CapacityArithmeticOverflow, result.Reasons);

        string[] forbidden = ["OrderIntent", "PortfolioReservation", "Execute", "Executor",
            "Private", "Activation"];
        Type[] types = [typeof(RiskEvaluationInput), typeof(RiskEvaluationConfiguration),
            typeof(RiskEvaluationResult), typeof(RiskEvaluationEngine)];
        foreach (Type type in types)
        {
            string names = string.Join("|", type.GetMembers(BindingFlags.Public
                | BindingFlags.Instance | BindingFlags.Static).Select(member => member.Name));
            Assert.DoesNotContain(forbidden, forbiddenName => names.Contains(forbiddenName,
                StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RiskEvaluationContractsRoundTripStrictlyAndRejectUnknownFields()
    {
        RiskEvaluationResult original = RiskEvaluationEngine.Evaluate(CreateInput(),
            CreateConfiguration(), new LinearPerpetualRiskMathCalculator());
        string json = CoreJsonSerializer.Serialize(original);
        RiskEvaluationResult restored = CoreJsonSerializer.Deserialize<RiskEvaluationResult>(json);

        Assert.Equal(original, restored);
        Assert.Contains("risk-evaluation-v1", json, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<RiskEvaluationResult>(
            json.Replace("\"reasons\":", "\"unknownReasons\":",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void ResultRejectsTamperedRiskDecisionIdentityAndVersionLinks()
    {
        RiskEvaluationResult original = RiskEvaluationEngine.Evaluate(CreateInput(),
            CreateConfiguration(), new LinearPerpetualRiskMathCalculator());
        RiskDecision tampered = new("risk-other", "decision-other",
            RiskDecisionOutcome.Approved, [RiskReasonCodes.Approved]);

        Assert.Throws<ArgumentException>(() => new RiskEvaluationResult(
            original.FormatVersion, new RiskEvaluationVersion("risk-evaluation-v1"),
            original.Input, original.Configuration, tampered, original.RiskMathResult,
            original.Checks, original.Reasons, original.Provenance));
    }

    [Fact]
    public void CalculatorVersionReadFailureBlocksInsteadOfEscapingTheRiskGate()
    {
        RiskEvaluationResult result = RiskEvaluationEngine.Evaluate(CreateInput(),
            CreateConfiguration(), new ThrowingVersionCalculator());

        Assert.False(result.IsApproved);
        Assert.Contains(RiskReasonCodes.RiskMathCalculationFailed, result.Reasons);
    }

    private static RiskEvaluationInput CreateInput(
        DecisionProjectionSnapshot? projection = null,
        CausalEventCursor? cursor = null,
        RiskMathInput? math = null,
        RiskProtectionEvidence? protection = null,
        RiskHealthEvidence? accountHealth = null,
        RiskHealthEvidence? portfolioHealth = null,
        RiskHealthEvidence? marketHealth = null,
        RiskKillSwitchEvidence? killSwitch = null,
        RiskPortfolioUsage? portfolioUsage = null,
        Money? candidateMargin = null,
        bool calculatorMustBeCalled = true)
    {
        projection ??= DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked: false);
        cursor ??= projection.CausalCursor;
        math ??= new RiskMathInput(Instrument, Direction.Long,
            new Money(1_000m, "USDT"), 0.01m, new Price(100m), new Price(90m),
            new MoneyPerQuantityUnit(0m, "USDT"),
            new MoneyPerQuantityUnit(0m, "USDT"));
        protection ??= new RiskProtectionEvidence(Instrument, math.Direction,
            projection.Result.Decision.SetupId, "method-v1", "source-v1", "protective-v1",
            RiskEvidenceFreshness.Fresh, cursor,
            math.Direction == Direction.Long ? new Price(90m) : new Price(110m),
            math.Direction == Direction.Long ? new Price(110m) : new Price(90m),
            math.Direction == Direction.Long ? new Price(120m) : new Price(80m), "fixture");
        accountHealth ??= RiskHealthEvidence.Healthy("account-source", "account-v1",
            cursor, "fixture");
        portfolioHealth ??= RiskHealthEvidence.Healthy("portfolio-source", "portfolio-v1",
            cursor, "fixture");
        marketHealth ??= RiskHealthEvidence.Healthy("market-source", "market-v1",
            cursor, "fixture");
        killSwitch ??= RiskKillSwitchEvidence.Clear("kill-source", "kill-v1", cursor,
            "fixture");
        portfolioUsage ??= new RiskPortfolioUsage(Portfolio, new Money(0m, "USDT"),
            new Money(0m, "USDT"), new Money(0m, "USDT"), 0, "usage-source", "usage-v1",
            cursor, "fixture");
        return new RiskEvaluationInput("risk-fail-closed", projection, math, Portfolio, cursor,
            portfolioUsage, new Money(0m, "USDT"), "period-2026-01-01",
            candidateMargin ?? new Money(10m, "USDT"), 1, accountHealth, portfolioHealth,
            marketHealth, killSwitch, "fixture", protection,
            RiskCooldownState.Clear(Instrument, "cooldown-source", "cooldown-v1", cursor,
                "fixture"));
    }

    private static RiskEvaluationConfiguration CreateConfiguration() => new(
        RiskEvaluationVersion.Current, new RiskMathVersion("linear-perpetual-risk-v1"),
        new RiskLimitConfiguration(new Money(10m, "USDT"), new Money(100m, "USDT"),
            new Money(100m, "USDT"), "period-2026-01-01"),
        new PortfolioLimits(Portfolio, new Money(100m, "USDT"),
            new Money(10_000m, "USDT"), 2), "fixture");

    private static DecisionProjectionSnapshot CreateShortProjection()
    {
        SetupEvidenceSnapshot evidence = LifecycleInputFixtures.All.First(item =>
            item.Evidence.Direction == Direction.Short
            && item.ExpectedState == SetupEvidenceState.Ready).Evidence;
        SetupLifecycleConfiguration lifecycleConfiguration =
            DecisionProjectionFixtures.CreateLifecycleConfiguration();
        SetupLifecycleIdentity identity = SetupLifecycleIdentity.Create(
            evidence.InputSequence.InstrumentId, evidence.SetupType, Direction.Short,
            "short-anchor", new UtcTimestamp(DateTimeOffset.Parse(
                "2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            "context-v1");
        SetupLifecycleSnapshot lifecycle = SetupLifecycleSnapshot.CreateInitial(identity,
            lifecycleConfiguration) with
        {
            State = SetupState.EntryArmed,
            ProcessedCursor = evidence.Cursor,
            ProcessedAvailabilityTime = evidence.Cursor.AsOf,
            ProcessedEvents = new ImmutableValueList<SetupLifecycleEventReceipt>([])
        };
        DecisionEngineConfiguration decisionConfiguration =
            DecisionProjectionFixtures.CreateDecisionConfiguration(DecisionEngineProfile.Allow);
        DecisionEngineResult result = DecisionEngine.Evaluate(new DecisionEngineInput(
            "decision-short", lifecycle, evidence,
            [new DecisionEvidence(DecisionLane.LaneB, DecisionEvidenceStatus.Confirm, "lane-b",
                "lane-b-v1", evidence.Cursor, "fixture")], "fixture"), decisionConfiguration);
        return DecisionProjectionSnapshot.Create(result, lifecycleConfiguration,
            decisionConfiguration);
    }

    private sealed class CountingCalculator : IRiskMathCalculator
    {
        private readonly bool _shouldThrow;

        public CountingCalculator(bool shouldThrow = false) => _shouldThrow = shouldThrow;

        public int Calls { get; private set; }

        public RiskMathVersion Version { get; } = new("linear-perpetual-risk-v1");

        public RiskMathResult Calculate(RiskMathInput input)
        {
            Calls++;
            if (_shouldThrow)
            {
                throw new InvalidOperationException("fixture failure");
            }

            return new LinearPerpetualRiskMathCalculator().Calculate(input);
        }
    }

    private sealed class ThrowingVersionCalculator : IRiskMathCalculator
    {
        public RiskMathVersion Version => throw new InvalidOperationException("fixture failure");

        public RiskMathResult Calculate(RiskMathInput input) => throw new InvalidOperationException(
            "fixture failure");
    }
}
