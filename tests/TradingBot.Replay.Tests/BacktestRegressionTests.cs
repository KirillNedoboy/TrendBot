using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class BacktestRegressionTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void IdenticalEventsProduceEquivalentCanonicalReportsAcrossInputPermutations()
    {
        ReplayEnvelope[] source =
        [
            Envelope("bar-1", 0, 100m, 101m, 99m, 100m, 1),
            Envelope("bar-2", 1, 101m, 105m, 100m, 104m, 2),
            Envelope("bar-3", 2, 104m, 106m, 103m, 105m, 3)
        ];
        ReplayRunResult leftRun = HistoricalReplayRunner.Run("stream-a", source,
            static context => context.Event.EventId);
        ReplayRunResult rightRun = HistoricalReplayRunner.Run("stream-a", source.Reverse(),
            static context => context.Event.EventId);
        ReplayEnvelope[] serializedSource = source
            .Select(static envelope => CoreJsonSerializer.Deserialize<ReplayEnvelope>(
                CoreJsonSerializer.Serialize(envelope)))
            .ToArray();
        ReplayRunResult serializedRun = HistoricalReplayRunner.Run("stream-a", serializedSource,
            static context => context.Event.EventId);

        BacktestReport left = BacktestReport.Create("run-left", leftRun);
        BacktestReport right = BacktestReport.Create("run-right", rightRun);
        BacktestReport serialized = BacktestReport.Create("run-serialized", serializedRun);

        BacktestRegressionComparison comparison = BacktestRegression.Compare(left, right);
        BacktestRegressionComparison serializedComparison = BacktestRegression.Compare(left,
            serialized);

        Assert.True(comparison.Equivalent);
        Assert.True(serializedComparison.Equivalent);
        Assert.Equal(left.DeterministicFingerprint, right.DeterministicFingerprint);
        Assert.Equal(BacktestReportLimitations.Default, left.Limitations);
        Assert.Contains("intrabar", left.CanonicalJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("actual fills", left.CanonicalJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("true point of control", left.CanonicalJson,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportRetainsCausalReferencesDiagnosticsCostsAndOpenRemainder()
    {
        ReplayEnvelope[] source =
        [
            Envelope("bar-1", 0, 100m, 101m, 99m, 100m, 1),
            Envelope("bar-2", 1, 101m, 102m, 100m, 101m, 2)
        ];
        ReplayRunResult run = HistoricalReplayRunner.Run("stream-a", source,
            static context => context.Event.EventId);
        BacktestReport report = BacktestReport.Create("run-1", run,
            configuration: [new BacktestConfigurationEntry("profile", "research-v1")]);

        Assert.Equal("run-1", report.RunId);
        Assert.Equal(2, report.Events.Count);
        Assert.Equal(1, report.Events[0].StableSequence);
        Assert.Equal(2, report.Events[1].StableSequence);
        Assert.Contains(report.Configuration,
            static item => item.Name == "profile" && item.Value == "research-v1");
        Assert.Contains(report.Diagnostics,
            static diagnostic => diagnostic.Contains("none", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0m, report.TotalFees);
        Assert.Equal(0m, report.TotalSlippage);
        Assert.Equal(0m, report.RemainingQuantity);
        Assert.False(report.IsOpen);
    }

    [Fact]
    public void ReportRetainsInjectedFillModelVersionAlongsideNumericAssumptions()
    {
        ReplayEnvelope[] source =
        [
            Envelope("signal", 0, 100m, 101m, 99m, 100m, 1),
            Envelope("entry", 1, 101m, 102m, 100m, 101m, 2)
        ];
        LinearPerpetualRiskMathCalculator calculator = new();
        RiskMathInput input = new(Instrument, Direction.Long,
            new Money(1000m, "USDT"), 0.02m, new Price(100m), new Price(95m),
            new MoneyPerQuantityUnit(0.1m, "USDT"),
            new MoneyPerQuantityUnit(0.2m, "USDT"));
        SimulatedRiskPlan plan = new(
            "report-plan", Instrument, Direction.Long, Utc(0), input, [], null, false,
            null, new SimulatedFillAssumptions(0.1m, 0.2m,
                new FillSimulationAssumptions("fees", "slippage", "next bar",
                    "unknown queue", "none", "stop before target", "OHLCV limits"),
                new FillSimulationVersion("assumptions-v1")),
            new Decision("report-decision", "setup-1", DecisionOutcome.Allow,
                ["fixture"]));
        SimulatedRiskSimulationResult simulation = new SimulatedRiskSimulator(calculator)
            .Simulate(plan, source);
        ReplayRunResult run = HistoricalReplayRunner.Run("stream-a", source,
            static context => context.Event.EventId);

        BacktestReport report = BacktestReport.Create("run-report", run, simulation);

        Assert.Equal(new FillSimulationVersion("ohlcv-fill-v1"),
            report.FillSimulationVersion);
        Assert.Contains("ohlcv-fill-v1", report.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("assumptions-v1", report.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("fees", report.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AtomicJournalWriteReplacesCompletedReportWithoutPartialContent()
    {
        ReplayEnvelope[] source = [Envelope("bar-1", 0, 100m, 101m, 99m, 100m, 1)];
        ReplayRunResult run = HistoricalReplayRunner.Run("stream-a", source,
            static context => context.Event.EventId);
        BacktestReport report = BacktestReport.Create("run-1", run);
        string directory = Path.Combine(Path.GetTempPath(), "trading-bot-backtest-tests");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "report.json");

        try
        {
            BacktestReportJournal.WriteAtomic(report, path);
            string first = File.ReadAllText(path);
            BacktestReportJournal.WriteAtomic(report with { RunId = "run-2" }, path);
            string second = File.ReadAllText(path);

            Assert.Equal(report.CanonicalJson, first);
            Assert.NotEqual(first, second);
            Assert.DoesNotContain(".tmp", second, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ReportRetainsCoreLifecycleAndDecisionReferences()
    {
        ReplayEnvelope[] source = [Envelope("bar-1", 0, 100m, 101m, 99m, 100m, 1)];
        ReplayRunResult run = HistoricalReplayRunner.Run("stream-a", source,
            static context => context.Event.EventId);
        SetupLifecycleConfiguration lifecycleConfiguration = new(
            "lifecycle-rules-v1", "lifecycle-config-v1", requireExternalPlan: false,
            requireConfirmationWindow: false);
        SetupLifecycleIdentity identity = SetupLifecycleIdentity.Create(
            Instrument, SetupType.BoundaryRejection, Direction.Long, "anchor-1",
            Utc(0), "context-v1");
        SetupLifecycleSnapshot initial = SetupLifecycleSnapshot.CreateInitial(identity,
            lifecycleConfiguration);
        SetupLifecycleInput lifecycleInput = new(SetupLifecycleInput.CurrentFormatVersion,
            "stream-a", "context-1", SetupLifecycleFactKind.ContextReady, identity,
            Utc(0), Utc(0), Utc(0), 1, "lifecycle-rules-v1",
            "lifecycle-config-v1", "fixture");
        SetupLifecycleTransition transition = new SetupLifecycleEngine(
            lifecycleConfiguration).Apply(initial, lifecycleInput);
        Decision decision = new("decision-1", identity.Fingerprint,
            DecisionOutcome.Allow, ["fixture-reason"]);

        BacktestReport report = BacktestReport.Create("run-core", run,
            decisions: [decision], transitions: [transition],
            setupIdentities: [identity]);

        Assert.Equal(identity.Fingerprint, Assert.Single(report.SetupIdentities));
        Assert.Equal("decision-1", Assert.Single(report.Decisions).DecisionId);
        Assert.Equal(SetupLifecycleTransitionStatus.Applied,
            Assert.Single(report.Transitions).Status);
        Assert.Contains("fixture-reason", report.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains(identity.Fingerprint, report.CanonicalJson, StringComparison.Ordinal);
    }

    private static ReplayEnvelope Envelope(string id, int minutes, decimal open,
        decimal high, decimal low, decimal close, long sequence) => new("stream-a",
        new CausalMarketEvent(new Bar(id, Instrument, Utc(minutes), Utc(minutes),
            Utc(minutes + 1), new Price(open), new Price(high), new Price(low),
            new Price(close), new Quantity(1m)), Utc(minutes + 1),
            Utc(minutes + 1), sequence));

    private static UtcTimestamp Utc(int minutes) => new(BaseTime.AddMinutes(minutes));
}
