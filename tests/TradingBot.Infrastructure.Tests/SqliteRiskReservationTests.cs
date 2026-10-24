using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Core.Tests;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqliteRiskReservationTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void VersionTenMigrationAddsRiskEvidenceTable()
    {
        using TemporaryDatabase database = new();
        using (SqlitePortfolioReservationStore store =
               SqlitePortfolioReservationStore.Open(database.Path))
        {
            Assert.Null(store.FindRiskEvaluation("missing-evaluation"));
        }

        using (SqliteConnection connection = OpenRaw(database.Path))
        {
            ExecuteNonQuery(connection, "DROP TABLE risk_evaluations; PRAGMA user_version = 10;");
        }

        using SqlitePortfolioReservationStore reopened =
            SqlitePortfolioReservationStore.Open(database.Path);
        using SqliteConnection verify = OpenRaw(database.Path);
        Assert.Equal(14L, ExecuteScalar<long>(verify, "PRAGMA user_version;"));
        Assert.Equal(0L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM risk_evaluations;"));
    }

    [Fact]
    public void ApprovedRiskReservationPersistsEvidenceAndSurvivesRestart()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-risk");
        PortfolioLimits limits = new(portfolioId, new Money(100m, "USDT"),
            new Money(10_000m, "USDT"), 2);
        IRiskMathCalculator calculator = new LinearPerpetualRiskMathCalculator();

        OrderIntent intent = CreateIntent("intent-risk", "client-risk");
        RiskEvaluationResult evaluation = CreateEvaluation(intent, portfolioId, calculator);
        PortfolioReservation reservation = CreateReservation(evaluation, portfolioId, intent,
            "reservation-risk");

        using (SqlitePortfolioReservationStore store = SqlitePortfolioReservationStore.Open(
                   database.Path))
        {
            Assert.True(store.Register(limits));
            Assert.Equal(ReconciliationStatus.Acknowledged,
                store.AcknowledgeReconciliation(portfolioId, 0).Status);

            Assert.Equal(PortfolioReservationResult.Reserved,
                store.TryReserve(intent, reservation, evaluation, 0, calculator));
            Assert.Equal(evaluation, store.FindRiskEvaluation(evaluation.Input.EvaluationId));
        }

        using SqlitePortfolioReservationStore reopened = SqlitePortfolioReservationStore.Open(
            database.Path);
        Assert.Equal(evaluation, reopened.FindRiskEvaluation(evaluation.Input.EvaluationId));
        Assert.Equal(reservation, reopened.FindReservation(reservation.ReservationId));
    }

    [Fact]
    public void BlockedRiskCannotReserveAndLeavesNoPartialRows()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-blocked");
        PortfolioLimits limits = new(portfolioId, new Money(100m, "USDT"),
            new Money(10_000m, "USDT"), 2);
        IRiskMathCalculator calculator = new LinearPerpetualRiskMathCalculator();
        OrderIntent intent = CreateIntent("intent-blocked", "client-blocked");
        RiskEvaluationResult evaluation = CreateEvaluation(intent, portfolioId, calculator,
            blocked: true);
        PortfolioReservation reservation = CreateReservation(evaluation, portfolioId, intent,
            "reservation-blocked");

        using SqlitePortfolioReservationStore store = SqlitePortfolioReservationStore.Open(
            database.Path);
        Assert.True(store.Register(limits));
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);

        Assert.Throws<InvalidOperationException>(() =>
            store.TryReserve(intent, reservation, evaluation, 0, calculator));
        Assert.Null(store.FindReservation(reservation.ReservationId));
        Assert.Null(SqliteOrderIntentJournal.Open(database.Path).Find(intent.IntentId));
        Assert.Null(store.FindRiskEvaluation(evaluation.Input.EvaluationId));
    }

    [Fact]
    public void RecomputedRiskMismatchCannotWriteAnyRows()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-recompute");
        PortfolioLimits limits = new(portfolioId, new Money(100m, "USDT"),
            new Money(10_000m, "USDT"), 2);
        IRiskMathCalculator calculator = new LinearPerpetualRiskMathCalculator();
        OrderIntent intent = CreateIntent("intent-recompute", "client-recompute");
        RiskEvaluationResult evaluation = CreateEvaluation(intent, portfolioId, calculator);
        PortfolioReservation reservation = CreateReservation(evaluation, portfolioId, intent,
            "reservation-recompute");

        using SqlitePortfolioReservationStore store = SqlitePortfolioReservationStore.Open(
            database.Path);
        Assert.True(store.Register(limits));
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);

        Assert.Throws<InvalidOperationException>(() =>
            store.TryReserve(intent, reservation, evaluation, 0, new WrongVersionCalculator()));
        Assert.Null(store.FindReservation(reservation.ReservationId));
        Assert.Null(SqliteOrderIntentJournal.Open(database.Path).Find(intent.IntentId));
        Assert.Null(store.FindRiskEvaluation(evaluation.Input.EvaluationId));
    }

    [Fact]
    public void IdenticalRiskRetryIsIdempotentButDecisionIdentityCannotMove()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-identity");
        PortfolioLimits limits = new(portfolioId, new Money(100m, "USDT"),
            new Money(10_000m, "USDT"), 2);
        IRiskMathCalculator calculator = new LinearPerpetualRiskMathCalculator();
        OrderIntent intent = CreateIntent("intent-identity", "client-identity");
        RiskEvaluationResult evaluation = CreateEvaluation(intent, portfolioId, calculator);
        PortfolioReservation reservation = CreateReservation(evaluation, portfolioId, intent,
            "reservation-identity");

        using SqlitePortfolioReservationStore store = SqlitePortfolioReservationStore.Open(
            database.Path);
        Assert.True(store.Register(limits));
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);
        Assert.Equal(PortfolioReservationResult.Reserved,
            store.TryReserve(intent, reservation, evaluation, 0, calculator));
        Assert.Equal(PortfolioReservationResult.AlreadyReserved,
            store.TryReserve(intent, reservation, evaluation, 0, calculator));

        OrderIntent conflictingIntent = CreateIntent("intent-conflict", "client-conflict");
        PortfolioReservation conflictingReservation = CreateReservation(evaluation, portfolioId,
            conflictingIntent, "reservation-conflict");
        Assert.Throws<InvalidOperationException>(() =>
            store.TryReserve(conflictingIntent, conflictingReservation, evaluation, 0, calculator));
        Assert.Null(store.FindReservation(conflictingReservation.ReservationId));
        Assert.Null(SqliteOrderIntentJournal.Open(database.Path).Find(conflictingIntent.IntentId));
    }

    [Fact]
    public void ReservationRequiresCurrentAcknowledgedRevisionWithoutUnknownOrManualState()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-revision");
        PortfolioLimits limits = new(portfolioId, new Money(100m, "USDT"),
            new Money(10_000m, "USDT"), 2);
        IRiskMathCalculator calculator = new LinearPerpetualRiskMathCalculator();
        OrderIntent intent = CreateIntent("intent-revision", "client-revision");
        RiskEvaluationResult evaluation = CreateEvaluation(intent, portfolioId, calculator);
        PortfolioReservation reservation = CreateReservation(evaluation, portfolioId, intent,
            "reservation-revision");

        using SqlitePortfolioReservationStore store = SqlitePortfolioReservationStore.Open(
            database.Path);
        Assert.True(store.Register(limits));
        Assert.Throws<InvalidOperationException>(() =>
            store.TryReserve(intent, reservation, evaluation, 0, calculator));

        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);
        Assert.Equal(PortfolioReservationResult.Reserved,
            store.TryReserve(intent, reservation, evaluation, 0, calculator));
    }

    private static RiskEvaluationResult CreateEvaluation(OrderIntent intent,
        PortfolioId portfolioId, IRiskMathCalculator calculator, bool blocked = false)
    {
        DecisionProjectionSnapshot projection = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked);
        CausalEventCursor cursor = projection.CausalCursor;
        RiskMathInput math = new(intent.InstrumentId, intent.Direction,
            new Money(1_000m, "USDT"), 0.01m, new Price(100m), new Price(90m),
            new MoneyPerQuantityUnit(0m, "USDT"),
            new MoneyPerQuantityUnit(0m, "USDT"));
        RiskProtectionEvidence protection = new(intent.InstrumentId, intent.Direction,
            projection.Result.Decision.SetupId, "protective-method-v1", "protective-source-v1",
            "protective-v1", RiskEvidenceFreshness.Fresh, cursor, new Price(90m),
            new Price(110m), new Price(120m), "fixture");
        RiskEvaluationInput input = new(
            $"evaluation-{intent.IntentId}", projection, math, portfolioId, cursor,
            new RiskPortfolioUsage(portfolioId, new Money(0m, "USDT"),
                new Money(0m, "USDT"), new Money(0m, "USDT"), 0, "usage-source", "usage-v1",
                cursor, "fixture"),
            new Money(0m, "USDT"), "period-2026-01-01", new Money(10m, "USDT"), 1,
            RiskHealthEvidence.Healthy("account-source", "account-v1", cursor, "fixture"),
            RiskHealthEvidence.Healthy("portfolio-source", "portfolio-v1", cursor, "fixture"),
            RiskHealthEvidence.Healthy("market-source", "market-v1", cursor, "fixture"),
            RiskKillSwitchEvidence.Clear("kill-source", "kill-v1", cursor, "fixture"),
            "fixture", protection,
            RiskCooldownState.Clear(intent.InstrumentId, "cooldown-source", "cooldown-v1",
                cursor, "fixture"));
        RiskEvaluationConfiguration configuration = new(
            RiskEvaluationVersion.Current, calculator.Version,
            new RiskLimitConfiguration(new Money(10m, "USDT"), new Money(100m, "USDT"),
                new Money(100m, "USDT"), "period-2026-01-01"),
            new PortfolioLimits(portfolioId, new Money(100m, "USDT"),
                new Money(10_000m, "USDT"), 2), "fixture");
        return RiskEvaluationEngine.Evaluate(input, configuration, calculator);
    }

    private static OrderIntent CreateIntent(string intentId, string clientOrderId) =>
        new(intentId, "strategy-run-1",
            DecisionProjectionFixtures.CreateProjection(DecisionEngineProfile.Allow)
                .Result.Decision.SetupId,
            clientOrderId, Instrument,
            Direction.Long, OrderRole.Entry, 1, new Quantity(1m), new Price(100m));

    private static PortfolioReservation CreateReservation(RiskEvaluationResult evaluation,
        PortfolioId portfolioId, OrderIntent intent, string reservationId) =>
        new(reservationId, portfolioId, intent.IntentId,
            evaluation.RiskMathResult!.RiskBudget,
            evaluation.RiskMathResult.NotionalExposure,
            evaluation.Input.CandidatePositionSlots!.Value);

    private sealed class WrongVersionCalculator : IRiskMathCalculator
    {
        public RiskMathVersion Version { get; } = new("wrong-risk-v1");

        public RiskMathResult Calculate(RiskMathInput input) =>
            throw new InvalidOperationException("This calculator must not be used.");
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tradingbot-risk-reservation-{Guid.NewGuid():N}.sqlite");

        public string Path { get; }

        public void Dispose()
        {
            foreach (string candidate in new[] { Path, $"{Path}-wal", $"{Path}-shm" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
        }
    }

    private static SqliteConnection OpenRaw(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
