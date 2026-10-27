using TradingBot.App;
using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class PromotionEvidenceBundleTests
{
    [Fact]
    public void BundlePreservesFrozenIdentitiesAndExplicitEvidenceBlockers()
    {
        Fixture fixture = CreateFixture();
        FrozenOosEvaluationReport report = Evaluate(fixture);

        PromotionEvidenceBundle bundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, report);

        Assert.Equal(fixture.Definition.Identity, bundle.DefinitionIdentity);
        Assert.Equal(report.Identity, bundle.OosReportIdentity);
        Assert.Equal(fixture.Research.Identity, bundle.ResearchManifestIdentity);
        Assert.Equal(fixture.Criteria.Identity, bundle.CriteriaIdentity);
        Assert.Equal(fixture.Dataset.Identity, bundle.MultiSourceManifestIdentity);
        Assert.Equal(fixture.Definition.ConfigurationIdentity, bundle.ConfigurationIdentity);
        Assert.Equal(fixture.Models.Identity, bundle.FrozenModelVersionsIdentity);
        Assert.Equal(report.CanonicalJson, bundle.OosReportCanonicalJson);
        Assert.Equal(fixture.Research.CanonicalJson, bundle.ResearchManifestCanonicalJson);
        Assert.Equal(fixture.Criteria.CanonicalJson, bundle.CriteriaCanonicalJson);
        Assert.Equal(fixture.Dataset.Identity,
            CoreJsonSerializer.Deserialize<MultiSourceDatasetManifest>(
                bundle.MultiSourceManifestCanonicalJson).Identity);
        PromotionEvidenceSnapshot quality = bundle.Find(PromotionEvidenceCategory.DataQuality)
            ?? throw new Xunit.Sdk.XunitException("Data quality evidence was not retained.");
        PromotionEvidenceSnapshot operational =
            bundle.Find(PromotionEvidenceCategory.OperationalShadow)
            ?? throw new Xunit.Sdk.XunitException("Operational shadow evidence was not retained.");
        Assert.Equal(PromotionEvidenceStatus.Unknown, quality.Status);
        Assert.Equal(PromotionEvidenceStatus.Missing, operational.Status);
        Assert.Contains("operational-shadow-evidence-not-provided", bundle.Blockers);
        Assert.Contains("lane-c-quality-report-not-provided", bundle.Unknowns);
        Assert.Contains("reconciliation-unacknowledged", bundle.Blockers);
        Assert.Contains("synthetic-fixture-only", bundle.Provenance);
        Assert.True(bundle.HasUnresolvedBlockers);
    }

    [Fact]
    public void CanonicalBundleRoundTripsAndRejectsTampering()
    {
        Fixture fixture = CreateFixture();
        FrozenOosEvaluationReport report = Evaluate(fixture);
        PromotionEvidenceBundle first = PromotionEvidenceBundleBuilder.Build(
            new PromotionEvidenceBundleInput(fixture.Definition, report));

        PromotionEvidenceBundle restored = PromotionEvidenceBundle.Deserialize(first.CanonicalJson);
        Assert.Equal(first.BundleIdentity, restored.BundleIdentity);
        Assert.Equal(first.CanonicalJson, restored.CanonicalJson);
        Assert.Equal(first.CanonicalJson, CoreJsonSerializer.Serialize(first));

        string tampered = first.CanonicalJson.Replace(first.OosReportIdentity,
            "tampered-oos-report", StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => PromotionEvidenceBundle.Deserialize(tampered));
    }

    [Fact]
    public void ChangedOosModelInvalidatesAffectedBundleWithoutMutatingTheSnapshot()
    {
        Fixture fixture = CreateFixture();
        FrozenOosEvaluationReport report = Evaluate(fixture);
        PromotionEvidenceBundle bundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, report);
        FrozenOosModelVersions changedModels = new([new("strategy", "strategy-v2")]);
        FrozenOosEvaluationReport changedReport = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, fixture.Criteria, fixture.Dataset,
            fixture.Records, "config-v1", fixture.Configuration, changedModels,
            fixture.Assessments, fixture.Reconciliation, static _ => "changed");

        PromotionEvidenceValidationResult validation = bundle.ValidateAgainst(
            fixture.Definition, changedReport);

        Assert.False(validation.IsValid);
        Assert.Contains("oos-report", validation.InvalidatedEvidence);
        Assert.NotEqual(bundle.OosReportIdentity, changedReport.Identity);
        Assert.Equal(report.Identity, bundle.OosReportIdentity);
        Assert.Contains("oos-report-changed", validation.Reasons);
    }

    [Fact]
    public void IndependentOperationalSnapshotsAreCanonicalAndKeepNativeIdsSeparate()
    {
        Fixture fixture = CreateFixture();
        FrozenOosEvaluationReport report = Evaluate(fixture);
        PromotionEvidenceSnapshot shadow = PromotionEvidenceSnapshot.FromArtifact(
            PromotionEvidenceCategory.OperationalShadow, "shadow-run-v1",
            "{\"synthetic\":true}",
            [new("streamId", "shadow-stream"), new("exchangeTime", null)],
            provenance: ["synthetic-fixture-only"],
            blockers: ["operational-shadow-not-run"]);
        PromotionEvidenceBundle bundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, report,
            new PromotionEvidenceBundleSources(additionalEvidence: [shadow]));

        PromotionEvidenceSnapshot restored = bundle.Find(PromotionEvidenceCategory.OperationalShadow)
            ?? throw new Xunit.Sdk.XunitException("Operational shadow evidence was not retained.");
        Assert.Equal("shadow-run-v1", restored.NativeIdentity);
        Assert.NotEqual(restored.NativeIdentity, restored.ContentHash);
        Assert.Null(restored.CausalFields.Single(field => field.Name == "exchangeTime").Value);
        Assert.Contains("operational-shadow-not-run", restored.Blockers);
    }

    private static FrozenOosEvaluationReport Evaluate(Fixture fixture) =>
        FrozenOosEvaluator.Evaluate(fixture.Definition, fixture.Research, fixture.Criteria,
            fixture.Dataset, fixture.Records, "config-v1", fixture.Configuration, fixture.Models,
            fixture.Assessments, fixture.Reconciliation, static _ => "stable trace");

    private static Fixture CreateFixture()
    {
        ReplayEnvelope envelope = new("trades", new CausalMarketEvent(
            new Trade("trade-1", new InstrumentId("BTCUSDT"), Utc(0), new Price(100m),
                new Quantity(1m), TradeSide.Buy), Utc(0), Utc(0), 1));
        MultiSourceReplayRecord record = new(new MultiSourceReplayOrderEntry("trades", "trade-1",
            1, 1, Utc(0), null, Utc(0), Utc(0)), envelope);
        BacktestConfigurationEntry[] configuration = [new("profile", "fixed")];
        FrozenResearchManifest research = FrozenResearchManifest.Create(
            "oos-fixture", "dataset-v1", "synthetic fixture", ["trades"], ["excluded-row"],
            [envelope], "config-v1", configuration, [new("selection", "fixed")], "risk-v1",
            "fill-v1", ["synthetic only"]);
        PredeclaredPromotionCriteria criteria = PredeclaredPromotionCriteria.Create(research,
            [new("causal-replay", "Is the replay causal?", ["trace"])], []);
        MultiSourceDatasetManifest dataset = MultiSourceDatasetManifest.Create("dataset-fixture",
            [new("trades", MultiSourceLaneKind.Trades, "source-manifest", "quality-proof", [],
                MultiSourceCoverage.Complete, MultiSourceQuality.Ready, [], 1, Utc(0), Utc(0),
                Utc(0), Utc(0), 1, 1)],
            [new("trades", "trade-1", 1, 1, Utc(0), null, Utc(0), Utc(0))]);
        FrozenOosModelVersions models = new([new("strategy", "strategy-v1")]);
        FrozenOosEvaluationDefinition definition = FrozenOosEvaluationDefinition.Create(
            research, criteria, dataset, [record], models);
        FrozenOosCriterionAssessment[] assessments =
        [
            new("causal-replay", FrozenOosCriterionStatus.Supported,
                [new("trace", "trace-evidence")], [])
        ];
        ReconciliationCheckpoint reconciliation = new(new PortfolioId("fixture"), 1,
            [], [], ["unknown-marker"], [], ReconciliationStatus.Unreconciled);
        return new Fixture(research, criteria, dataset, definition, [record], configuration,
            models, assessments, reconciliation);
    }

    private static UtcTimestamp Utc(int minutes) =>
        new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddMinutes(minutes));

    private sealed record Fixture(FrozenResearchManifest Research,
        PredeclaredPromotionCriteria Criteria, MultiSourceDatasetManifest Dataset,
        FrozenOosEvaluationDefinition Definition, IReadOnlyList<MultiSourceReplayRecord> Records,
        IReadOnlyList<BacktestConfigurationEntry> Configuration, FrozenOosModelVersions Models,
        IReadOnlyList<FrozenOosCriterionAssessment> Assessments,
        ReconciliationCheckpoint Reconciliation);
}
