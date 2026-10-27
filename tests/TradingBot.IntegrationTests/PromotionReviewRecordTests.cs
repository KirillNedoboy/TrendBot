using TradingBot.App;
using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class PromotionReviewRecordTests
{
    [Fact]
    public void ReviewAndEvaluateRequireAnExplicitVerdict()
    {
        System.Reflection.MethodInfo review = typeof(PromotionReviewRecordBuilder).GetMethods()
            .Single(value => value.Name == nameof(PromotionReviewRecordBuilder.Review)
                && value.GetParameters().Length == 4);
        System.Reflection.MethodInfo evaluate = typeof(PromotionReviewRecordBuilder).GetMethods()
            .Single(value => value.Name == nameof(PromotionReviewRecordBuilder.Evaluate)
                && value.GetParameters().Length == 3);

        Assert.False(review.GetParameters()[1].HasDefaultValue);
        Assert.False(evaluate.GetParameters()[1].HasDefaultValue);
    }

    [Fact]
    public void ReviewRecordRetainsLinkedBundleIdentitiesEvidenceStatusesAndNullFields()
    {
        Fixture fixture = CreateFixture();
        FrozenOosEvaluationReport report = Evaluate(fixture);
        PromotionEvidenceSnapshot operationalShadow = PromotionEvidenceSnapshot.FromArtifact(
            PromotionEvidenceCategory.OperationalShadow, "shadow-native-v1",
            "{\"synthetic\":true}",
            [new("exchangeTime", null)], provenance: ["synthetic-fixture-only"],
            blockers: ["operational-shadow-is-synthetic"]);
        PromotionEvidenceBundle bundle = PromotionEvidenceBundleBuilder.Build(fixture.Definition,
            report, new PromotionEvidenceBundleSources(additionalEvidence: [operationalShadow]));

        PromotionReviewRecord record = PromotionReviewRecordBuilder.Review(bundle,
            PromotionReviewVerdict.Blocked);

        Assert.Equal(PromotionReviewVerdict.Blocked, record.Verdict);
        Assert.Equal(bundle.BundleIdentity, record.BundleIdentity);
        Assert.Equal(bundle.DefinitionIdentity, record.DefinitionIdentity);
        Assert.Equal(bundle.OosReportIdentity, record.OosReportIdentity);
        Assert.Equal(bundle.CanonicalJson, record.BundleCanonicalJson);
        Assert.Equal(bundle.Evidence.Count, record.EvidenceReferences.Count);
        PromotionReviewEvidenceReference reference = record.EvidenceReferences.Single(
            value => value.Category == PromotionEvidenceCategory.OperationalShadow);
        Assert.Equal(operationalShadow.SnapshotIdentity, reference.EvidenceIdentity);
        Assert.Equal(PromotionEvidenceStatus.Provided, reference.Status);
        Assert.Equal("shadow-native-v1", reference.NativeIdentity);
        Assert.Null(reference.CausalFields.Single(value => value.Name == "exchangeTime").Value);
        Assert.Contains("operational-shadow-is-synthetic", record.Blockers);
        Assert.Contains("synthetic-provenance-present", record.ReviewReasons);
    }

    [Fact]
    public void AcceptanceIsImpossibleForUnresolvedUnknownMissingOrSyntheticEvidence()
    {
        Fixture fixture = CreateFixture();
        PromotionEvidenceBundle bundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, Evaluate(fixture));

        PromotionReviewRecord record = PromotionReviewRecordBuilder.Review(bundle,
            PromotionReviewVerdict.AcceptedForReviewGate, reviewAttemptId: "attempt-accepted");

        Assert.Equal(PromotionReviewVerdict.Blocked, record.Verdict);
        Assert.Contains(record.ReviewReasons, value =>
            value.Contains("synthetic", StringComparison.OrdinalIgnoreCase));
        Assert.False(record.GrantsExecutionAuthority);
        Assert.False(record.CanExecute);
    }

    [Fact]
    public void CanonicalReviewRecordRoundTripsAndRejectsTampering()
    {
        Fixture fixture = CreateFixture();
        PromotionEvidenceBundle bundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, Evaluate(fixture));
        PromotionReviewRecord record = PromotionReviewRecordBuilder.Review(bundle,
            PromotionReviewVerdict.Blocked, reviewAttemptId: "round-trip");

        PromotionReviewRecord restored = PromotionReviewRecord.Deserialize(record.CanonicalJson);

        Assert.Equal(record.DecisionIdentity, restored.DecisionIdentity);
        Assert.Equal(record.CanonicalJson, restored.CanonicalJson);
        Assert.Equal(record.CanonicalJson, CoreJsonSerializer.Serialize(record));
        string tampered = record.CanonicalJson.Replace(record.BundleIdentity,
            "tampered-bundle", StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => PromotionReviewRecord.Deserialize(tampered));
    }

    [Fact]
    public void JournalPersistsReviewsAndInvalidationsSeparatelyWithIdempotentRetries()
    {
        Fixture fixture = CreateFixture();
        PromotionEvidenceBundle bundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, Evaluate(fixture));
        PromotionReviewRecord review = PromotionReviewRecordBuilder.Review(bundle,
            PromotionReviewVerdict.Blocked, reviewAttemptId: "journal-review");
        PromotionReviewInvalidationRecord invalidation =
            PromotionReviewInvalidationRecord.Create(review,
                changedContext: [new("definitionIdentity", review.DefinitionIdentity, "changed")],
                changedEvidence: [], changedCategories: [PromotionEvidenceCategory.DataQuality],
                reasons: ["frozen-definition-changed"]);

        using TemporaryJournal temporary = new();
        using (PromotionReviewJournal journal = PromotionReviewJournal.Open(temporary.Path))
        {
            Assert.True(journal.AppendReview(review));
            Assert.False(journal.AppendReview(review));
            Assert.Empty(journal.ReadInvalidations());
            Assert.True(journal.AppendInvalidation(invalidation));
            Assert.False(journal.AppendInvalidation(invalidation));
        }

        using PromotionReviewJournal reopened = PromotionReviewJournal.Open(temporary.Path);
        Assert.Single(reopened.ReadReviews());
        Assert.Single(reopened.ReadInvalidations());
        Assert.Equal(review.DecisionIdentity, reopened.ReadReviews().Single().DecisionIdentity);
        Assert.Equal(invalidation.InvalidationIdentity,
            reopened.ReadInvalidations().Single().InvalidationIdentity);
        Assert.Null(reopened.FindActiveReview(review.DecisionIdentity));
    }

    [Fact]
    public void InvalidationCapturesBundleIdentityWhenOnlyBundleProvenanceChanges()
    {
        Fixture fixture = CreateFixture();
        FrozenOosEvaluationReport report = Evaluate(fixture);
        PromotionEvidenceBundle originalBundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, report);
        PromotionEvidenceBundle currentBundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, report,
            new PromotionEvidenceBundleSources(provenance: "source-context-v2"));
        Assert.NotEqual(originalBundle.BundleIdentity, currentBundle.BundleIdentity);

        PromotionEvidenceValidationResult validation = originalBundle.ValidateAgainst(
            fixture.Definition, report,
            new PromotionEvidenceBundleSources(provenance: "source-context-v2"));
        Assert.True(validation.IsValid);

        PromotionReviewRecord original = PromotionReviewRecordBuilder.Review(originalBundle,
            PromotionReviewVerdict.Blocked, reviewAttemptId: "provenance-original");
        PromotionReviewInvalidationRecord invalidation =
            PromotionReviewInvalidationRecord.FromValidation(original, currentBundle, validation);

        Assert.Contains(invalidation.ChangedContext,
            value => value.Name == "bundleIdentity"
                && value.Before == originalBundle.BundleIdentity
                && value.After == currentBundle.BundleIdentity);
        Assert.Contains("bundle-identity-changed", invalidation.Reasons);
        Assert.Equal(originalBundle.BundleIdentity, invalidation.OriginalBundleIdentity);
        Assert.Equal(currentBundle.BundleIdentity, invalidation.CurrentBundleIdentity);
        Assert.Equal(originalBundle.CanonicalJson, original.BundleCanonicalJson);
    }

    [Fact]
    public void InvalidationCapturesSameNativeEvidenceContentChangesAndPreservesOriginal()
    {
        Fixture fixture = CreateFixture();
        FrozenOosEvaluationReport report = Evaluate(fixture);
        PromotionEvidenceSnapshot before = PromotionEvidenceSnapshot.FromArtifact(
            PromotionEvidenceCategory.OperationalShadow, "same-native-evidence",
            "{\"value\":\"before\"}", provenance: ["synthetic-fixture-only"],
            blockers: ["operational-shadow-is-synthetic"]);
        PromotionEvidenceSnapshot after = PromotionEvidenceSnapshot.FromArtifact(
            PromotionEvidenceCategory.OperationalShadow, "same-native-evidence",
            "{\"value\":\"after\"}", provenance: ["synthetic-fixture-only"],
            blockers: ["operational-shadow-is-synthetic"]);
        PromotionEvidenceBundle originalBundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, report,
            new PromotionEvidenceBundleSources(additionalEvidence: [before]));
        PromotionEvidenceBundle currentBundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, report,
            new PromotionEvidenceBundleSources(additionalEvidence: [after]));
        Assert.Equal(before.NativeIdentity, after.NativeIdentity);
        Assert.NotEqual(before.SnapshotIdentity, after.SnapshotIdentity);

        PromotionEvidenceValidationResult validation = originalBundle.ValidateAgainst(
            fixture.Definition, report,
            new PromotionEvidenceBundleSources(additionalEvidence: [after]));
        Assert.False(validation.IsValid);
        PromotionReviewRecord original = PromotionReviewRecordBuilder.Review(originalBundle,
            PromotionReviewVerdict.Blocked, reviewAttemptId: "content-original");
        PromotionReviewInvalidationRecord invalidation =
            PromotionReviewInvalidationRecord.FromValidation(original, currentBundle, validation);

        Assert.Contains(PromotionEvidenceCategory.OperationalShadow,
            invalidation.ChangedCategories);
        Assert.Contains(invalidation.ChangedEvidence,
            value => value.Name == "evidence:OperationalShadow");
        Assert.Equal(original.DecisionIdentity, invalidation.OriginalDecisionIdentity);
        Assert.Equal(originalBundle.CanonicalJson, original.BundleCanonicalJson);
        Assert.Equal(originalBundle.BundleIdentity, invalidation.OriginalBundleIdentity);
    }

    [Fact]
    public void ContentAddressedJournalIgnoresPartialTempsAndRejectsTamperedRows()
    {
        Fixture fixture = CreateFixture();
        PromotionEvidenceBundle bundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, Evaluate(fixture));
        PromotionReviewRecord review = PromotionReviewRecordBuilder.Review(bundle,
            PromotionReviewVerdict.Blocked, reviewAttemptId: "tamper-review");

        using TemporaryJournal temporary = new();
        using PromotionReviewJournal journal = PromotionReviewJournal.Open(temporary.Path);
        Assert.True(journal.AppendReview(review));
        Assert.Single(Directory.GetFiles(journal.ReviewJournalPath, "*.json"));
        Assert.Empty(Directory.GetFiles(journal.InvalidationJournalPath, "*.json"));

        File.WriteAllText(Path.Combine(journal.ReviewJournalPath,
            $".{review.DecisionIdentity}.partial.tmp"), "{\"partial\":true}");
        Assert.Single(journal.ReadReviews());

        string reviewPath = Directory.GetFiles(journal.ReviewJournalPath, "*.json").Single();
        File.WriteAllText(reviewPath, review.CanonicalJson.Replace(review.BundleIdentity,
            "tampered-bundle", StringComparison.Ordinal));
        Assert.ThrowsAny<Exception>(() => journal.ReadReviews());
        Assert.ThrowsAny<Exception>(() => journal.AppendReview(review));
    }

    [Fact]
    public void InvalidationRetainsChangedContextAndCannotReinstateAnOldDecision()
    {
        Fixture fixture = CreateFixture();
        FrozenOosEvaluationReport report = Evaluate(fixture);
        PromotionEvidenceBundle bundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, report);
        PromotionReviewRecord original = PromotionReviewRecordBuilder.Review(bundle,
            PromotionReviewVerdict.Blocked, reviewAttemptId: "original");

        FrozenOosModelVersions changedModels = new([new("strategy", "strategy-v2")]);
        FrozenOosEvaluationReport changedReport = FrozenOosEvaluator.Evaluate(
            fixture.Definition, fixture.Research, fixture.Criteria, fixture.Dataset,
            fixture.Records, "config-v1", fixture.Configuration, changedModels,
            fixture.Assessments, fixture.Reconciliation, static _ => "changed");
        PromotionEvidenceBundle changedBundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, changedReport);
        PromotionEvidenceValidationResult validation = bundle.ValidateAgainst(
            fixture.Definition, changedReport);

        PromotionReviewInvalidationRecord invalidation =
            PromotionReviewInvalidationRecord.FromValidation(original, changedBundle, validation);

        Assert.Equal(original.DecisionIdentity, invalidation.OriginalDecisionIdentity);
        Assert.Equal(original.BundleIdentity, invalidation.OriginalBundleIdentity);
        Assert.Equal(changedBundle.BundleIdentity, invalidation.CurrentBundleIdentity);
        Assert.NotEmpty(invalidation.ChangedContext);
        Assert.NotEmpty(invalidation.ChangedEvidence);
        Assert.NotEmpty(invalidation.ChangedCategories);
        Assert.Contains("oos-report-changed", invalidation.Reasons);

        using TemporaryJournal temporary = new();
        using PromotionReviewJournal journal = PromotionReviewJournal.Open(temporary.Path);
        Assert.True(journal.AppendReview(original));
        Assert.True(journal.AppendInvalidation(invalidation));
        Assert.Null(journal.FindActiveReview(original.DecisionIdentity));

        PromotionReviewRecord newReview = PromotionReviewRecordBuilder.Review(bundle,
            PromotionReviewVerdict.Blocked, reviewAttemptId: "new-review-after-invalidation");
        Assert.NotEqual(original.DecisionIdentity, newReview.DecisionIdentity);
        Assert.True(journal.AppendReview(newReview));
        Assert.Null(journal.FindActiveReview(original.DecisionIdentity));
        Assert.Equal(newReview.DecisionIdentity,
            journal.FindActiveReview(newReview.DecisionIdentity)?.DecisionIdentity);
    }

    [Fact]
    public void ExplicitRejectionRetainsReviewerReasonWithoutExecutionAuthority()
    {
        Fixture fixture = CreateFixture();
        PromotionEvidenceBundle bundle = PromotionEvidenceBundleBuilder.Build(
            fixture.Definition, Evaluate(fixture));

        PromotionReviewRecord record = PromotionReviewRecordBuilder.Review(bundle,
            PromotionReviewVerdict.Rejected, ["reviewer-rejected"], "rejected-attempt");

        Assert.Equal(PromotionReviewVerdict.Rejected, record.Verdict);
        Assert.Contains("reviewer-rejected", record.ReviewReasons);
        Assert.False(record.GrantsExecutionAuthority);
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

    private sealed class TemporaryJournal : IDisposable
    {
        public TemporaryJournal() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"tradingbot-promotion-review-{Guid.NewGuid():N}");

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed record Fixture(FrozenResearchManifest Research,
        PredeclaredPromotionCriteria Criteria, MultiSourceDatasetManifest Dataset,
        FrozenOosEvaluationDefinition Definition, IReadOnlyList<MultiSourceReplayRecord> Records,
        IReadOnlyList<BacktestConfigurationEntry> Configuration, FrozenOosModelVersions Models,
        IReadOnlyList<FrozenOosCriterionAssessment> Assessments,
        ReconciliationCheckpoint Reconciliation);
}
