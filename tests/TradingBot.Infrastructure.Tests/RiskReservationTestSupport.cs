using TradingBot.Core;
using TradingBot.Core.Tests;
using TradingBot.Infrastructure;

namespace TradingBot.Infrastructure.Tests;

internal static class RiskReservationTestSupport
{
    internal static PortfolioReservationResult Reserve(
        SqlitePortfolioReservationStore store, OrderIntent intent,
        PortfolioReservation reservation)
    {
        ReconciliationCheckpoint checkpoint = store.ReadCheckpoint(reservation.PortfolioId);
        if (checkpoint.Status != ReconciliationStatus.Acknowledged)
        {
            checkpoint = store.AcknowledgeReconciliation(reservation.PortfolioId,
                checkpoint.Revision);
        }

        PortfolioLimits limits = store.FindLimits(reservation.PortfolioId)
            ?? throw new InvalidOperationException("The fixture portfolio is not registered.");
        FixedRiskMathCalculator calculator = new(reservation.Risk, reservation.NotionalExposure,
            intent.Quantity, limits.MaxRisk.Currency);
        RiskEvaluationResult evaluation = CreateEvaluation(intent, reservation.PortfolioId,
            reservation, limits, calculator);
        return store.TryReserve(intent, reservation, evaluation, checkpoint.Revision, calculator);
    }

    private static RiskEvaluationResult CreateEvaluation(OrderIntent intent,
        PortfolioId portfolioId, PortfolioReservation reservation, PortfolioLimits limits,
        FixedRiskMathCalculator calculator)
    {
        DecisionProjectionSnapshot projection = CreateProjection($"decision-{intent.IntentId}");
        CausalEventCursor cursor = projection.CausalCursor;
        decimal entry = intent.LimitPrice?.Value ?? 100m;
        decimal stop = intent.Direction == Direction.Long ? entry - 10m : entry + 10m;
        RiskMathInput math = new(intent.InstrumentId, intent.Direction,
            new Money(1_000m, limits.MaxRisk.Currency), 0.01m, new Price(entry),
            new Price(stop), new MoneyPerQuantityUnit(0m, limits.MaxRisk.Currency),
            new MoneyPerQuantityUnit(0m, limits.MaxRisk.Currency));
        RiskProtectionEvidence protection = new(intent.InstrumentId, intent.Direction,
            projection.Result.Decision.SetupId, "protective-method-v1", "protective-source-v1",
            "protective-v1", RiskEvidenceFreshness.Fresh, cursor, new Price(stop),
            new Price(intent.Direction == Direction.Long ? entry + 10m : entry - 10m),
            new Price(intent.Direction == Direction.Long ? entry + 20m : entry - 20m),
            "fixture");
        RiskEvaluationInput input = new(
            $"evaluation-{intent.IntentId}", projection, math, portfolioId, cursor,
            new RiskPortfolioUsage(portfolioId, new Money(0m, limits.MaxRisk.Currency),
                new Money(0m, limits.MaxRisk.Currency), new Money(0m, limits.MaxRisk.Currency),
                0, "usage-source", "usage-v1", cursor, "fixture"),
            new Money(0m, limits.MaxRisk.Currency), "fixture-period",
            new Money(0m, limits.MaxRisk.Currency), reservation.PositionSlots,
            RiskHealthEvidence.Healthy("account-source", "account-v1", cursor, "fixture"),
            RiskHealthEvidence.Healthy("portfolio-source", "portfolio-v1", cursor, "fixture"),
            RiskHealthEvidence.Healthy("market-source", "market-v1", cursor, "fixture"),
            RiskKillSwitchEvidence.Clear("kill-source", "kill-v1", cursor, "fixture"),
            "fixture", protection,
            RiskCooldownState.Clear(intent.InstrumentId, "cooldown-source", "cooldown-v1",
                cursor, "fixture"));
        RiskEvaluationConfiguration configuration = new(
            RiskEvaluationVersion.Current, calculator.Version,
            new RiskLimitConfiguration(reservation.Risk,
                new Money(limits.MaxRisk.Amount, limits.MaxRisk.Currency),
                new Money(limits.MaxRisk.Amount, limits.MaxRisk.Currency), "fixture-period"),
            limits, "fixture");
        return RiskEvaluationEngine.Evaluate(input, configuration, calculator);
    }

    private static DecisionProjectionSnapshot CreateProjection(string decisionId)
    {
        SetupEvidenceSnapshot evidence = LifecycleInputFixtures.All[0].Evidence;
        SetupLifecycleIdentity identity = SetupLifecycleIdentity.Create(
            new InstrumentId("BTCUSDT"), SetupType.BoundaryRejection, Direction.Long,
            "projection-anchor", new UtcTimestamp(DateTimeOffset.Parse(
                "2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            "context-v1");
        SetupLifecycleConfiguration lifecycleConfiguration =
            DecisionProjectionFixtures.CreateLifecycleConfiguration();
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
            decisionId, lifecycle, evidence,
            [new DecisionEvidence(DecisionLane.LaneB, DecisionEvidenceStatus.Confirm,
                "lane-b", "lane-b-v1", evidence.Cursor, "fixture")],
            "fixture"), decisionConfiguration);
        return DecisionProjectionSnapshot.Create(result, lifecycleConfiguration,
            decisionConfiguration);
    }

    private sealed class FixedRiskMathCalculator : IRiskMathCalculator
    {
        private readonly Money _risk;
        private readonly Money _notional;
        private readonly Quantity _quantity;

        internal FixedRiskMathCalculator(Money risk, Money notional, Quantity quantity,
            string currency)
        {
            _risk = new Money(risk.Amount, currency);
            _notional = new Money(notional.Amount, currency);
            _quantity = new Quantity(quantity.Value);
        }

        public RiskMathVersion Version { get; } = new("fixture-risk-v1");

        public RiskMathResult Calculate(RiskMathInput input) =>
            new(Version, input, _risk, new MoneyPerQuantityUnit(1m, _risk.Currency),
                _quantity, _notional);
    }
}
