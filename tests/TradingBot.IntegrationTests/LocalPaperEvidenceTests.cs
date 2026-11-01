using System.Text.Json;
using TradingBot.App;
using TradingBot.Backtesting;
using TradingBot.Core;
using TradingBot.Core.Tests;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class LocalPaperEvidenceTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly PortfolioId Portfolio = new("portfolio-a");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LegacyRunIsCapturedAsFailClosedEvidence()
    {
        using TemporaryDatabase database = new();
        using LocalPaperStore store = LocalPaperStore.Open(database.Path);
        Assert.True(store.Append("stream-a", 1, "legacy-event", "payload", "state"));

        LocalPaperEvidence evidence = LocalPaperEvidence.Capture(store, "stream-a",
            "source-v1");

        Assert.Equal(LocalPaperEvidence.CurrentFormatVersion, evidence.FormatVersion);
        Assert.Equal(0, evidence.RealOrderCount);
        Assert.False(evidence.HasExecutionAuthority);
        Assert.NotEmpty(evidence.Blockers);
        Assert.NotEmpty(evidence.CanonicalJson);
        Assert.NotEmpty(evidence.Identity);
        Assert.Equal(PromotionEvidenceCategory.LocalPaper,
            evidence.ToPromotionEvidenceSnapshot().Category);
    }

    [Fact]
    public void DurableRunRoundTripsCanonicalEvidenceAndPromotionProjection()
    {
        using TemporaryDatabase database = new();
        LocalPaperConfiguration configuration = Configuration();
        using LocalPaperStore store = LocalPaperStore.Open(database.Path);
        LocalPaperExecutionResult execution = new LocalPaperExecutor(configuration, store,
            new LinearPerpetualRiskMathCalculator()).Process(new LocalPaperObservation(
            BarEnvelope("observation", 1, 100m, 101m, 99m, 100m, 1)));

        Assert.Equal(LocalPaperExecutionStatus.Recorded, execution.Status);
        LocalPaperEvidence evidence = LocalPaperEvidence.Capture(store, "stream-a",
            "source-v1", ["fixture"]);

        Assert.Single(evidence.Observations);
        Assert.Equal(1, evidence.Observations[0].Causal.StableSequence);
        Assert.Equal("source-v1", evidence.SourceIdentity);
        Assert.Equal(0, evidence.RealOrderCount);
        Assert.False(evidence.HasExecutionAuthority);
        Assert.Equal(evidence.CanonicalJson,
            LocalPaperEvidence.Deserialize(evidence.CanonicalJson).CanonicalJson);

        string tampered = evidence.CanonicalJson.Replace("source-v1", "source-v2",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => LocalPaperEvidence.Deserialize(tampered));
        PromotionEvidenceSnapshot snapshot = evidence.ToPromotionEvidenceSnapshot();
        Assert.Equal(PromotionEvidenceCategory.LocalPaper, snapshot.Category);
        Assert.Equal(PromotionEvidenceStatus.Provided, snapshot.Status);
        Assert.Contains("execution-authority-not-granted", snapshot.Blockers);
    }

    private static LocalPaperConfiguration Configuration()
    {
        RiskEvaluationConfiguration risk = new(RiskEvaluationVersion.Current,
            new RiskMathVersion("linear-perpetual-risk-v1"),
            new RiskLimitConfiguration(new Money(10m, "USDT"), new Money(100m, "USDT"),
                new Money(100m, "USDT"), "period-2026-01-01"),
            new PortfolioLimits(Portfolio, new Money(100m, "USDT"),
                new Money(10_000m, "USDT"), 2), "fixture");
        SimulatedFillAssumptions fills = new(0m, 0m,
            new FillSimulationAssumptions("fees", "slippage", "next bar", "queue",
                "unsupported partial fills", "stop before target", "synthetic OHLCV"),
            new FillSimulationVersion("ohlcv-fill-v1"));
        return new LocalPaperConfiguration("paper-config-v1", new Money(1_000m, "USDT"),
            0.01m, risk, fills);
    }

    private static ReplayEnvelope BarEnvelope(string id, int offset, decimal open,
        decimal high, decimal low, decimal close, long sequence)
    {
        UtcTimestamp eventTime = new(BaseTime.AddMinutes(offset));
        Bar bar = new(id, Instrument, eventTime, eventTime,
            new UtcTimestamp(eventTime.Value.AddMinutes(1)), new Price(open), new Price(high),
            new Price(low), new Price(close), new Quantity(1m));
        return new ReplayEnvelope("stream-a", new CausalMarketEvent(bar, eventTime, eventTime,
            sequence));
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"local-paper-evidence-red-{Guid.NewGuid():N}.sqlite");

        public string Path { get; }

        public void Dispose()
        {
            foreach (string path in new[] { Path, Path + "-wal", Path + "-shm" })
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
