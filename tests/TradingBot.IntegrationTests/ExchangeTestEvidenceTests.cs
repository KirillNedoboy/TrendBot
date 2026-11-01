using System.Text.Json;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class ExchangeTestEvidenceTests
{
    [Fact]
    public void EvidenceRoundTripsCanonicalRedactedSnapshotAndKeepsPromotionSeparated()
    {
        ExchangeTestEvidence evidence = ExchangeTestEvidence.Build(CreateSnapshot(),
            new ExchangeTestEvidenceCaptureOptions
            {
                RestartVerified = true,
                IdempotencyVerified = true,
                Provenance = ExchangeTestEvidenceProvenance.SyntheticFixture
            });

        Assert.Equal(ExchangeTestEvidence.CurrentFormatVersion, evidence.FormatVersion);
        Assert.Equal("exchange-test-run", evidence.RunId);
        Assert.Equal("binance-usdm-test", evidence.EnvironmentId);
        Assert.True(evidence.RedactionVerified);
        Assert.Equal(0, evidence.RealOrderCount);
        Assert.False(evidence.HasExecutionAuthority);
        Assert.False(evidence.ActivationAvailable);
        Assert.Contains("external-network-not-verified", evidence.Unknowns);
        Assert.DoesNotContain("fixture-listen-key", evidence.CanonicalJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-signature", evidence.CanonicalJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain("rawCredentialPayload", evidence.CanonicalJson,
            StringComparison.Ordinal);

        ExchangeTestEvidence reopened = ExchangeTestEvidence.Deserialize(evidence.CanonicalJson);

        Assert.Equal(evidence.Identity, reopened.Identity);
        Assert.Equal(evidence.CanonicalJson, reopened.CanonicalJson);
        Assert.Single(reopened.Lifecycles);
        Assert.Single(reopened.Events);
        Assert.Single(reopened.Fills);
        Assert.Single(reopened.Reservations);
        Assert.Equal(PromotionEvidenceCategory.ExchangeTest,
            reopened.ToPromotionEvidenceSnapshot().Category);
        Assert.DoesNotContain("local-paper", reopened.ToPromotionEvidenceSnapshot()
            .ReferencedIdentities);
    }

    [Fact]
    public void EvidenceRejectsTamperingAndUnknownWireMembers()
    {
        ExchangeTestEvidence evidence = ExchangeTestEvidence.Build(CreateSnapshot(),
            new ExchangeTestEvidenceCaptureOptions
            {
                RestartVerified = true,
                IdempotencyVerified = true
            });
        string tampered = evidence.CanonicalJson.Replace("exchange-test-run",
            "exchange-test-tampered", StringComparison.Ordinal);
        string unknown = evidence.CanonicalJson.TrimEnd('}')
            + ",\"unknownMember\":true}";

        Assert.Throws<JsonException>(() => ExchangeTestEvidence.Deserialize(tampered));
        Assert.Throws<JsonException>(() => ExchangeTestEvidence.Deserialize(unknown));
    }

    [Fact]
    public void EvidenceExportIsAtomicIdempotentAndDoesNotOverwriteDifferentContent()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"tradingbot-exchange-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            ExchangeTestEvidence evidence = ExchangeTestEvidence.Build(CreateSnapshot());
            string path = Path.Combine(directory, "exchange-test.json");

            Assert.True(ExchangeTestEvidenceExporter.Export(evidence, path));
            Assert.False(ExchangeTestEvidenceExporter.Export(evidence, path));

            ExchangeTestEvidence different = ExchangeTestEvidence.Build(
                CreateSnapshot(runId: "exchange-test-run-2"));
            Assert.Throws<InvalidOperationException>(() =>
                ExchangeTestEvidenceExporter.Export(different, path));
            Assert.Throws<InvalidOperationException>(() =>
                ExchangeTestEvidenceExporter.Export(evidence,
                    Path.Combine(directory, "evidence.sqlite")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CaptureReopensOnePersistedSnapshotAndPreservesIdempotentEvidence()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"tradingbot-exchange-capture-{Guid.NewGuid():N}.sqlite");
        PortfolioId portfolioId = new("evidence-portfolio");
        ExchangeTestRunMetadata metadata = Metadata("capture-run");
        OrderIntent intent = CreateIntent("capture-intent", "capture-client");
        try
        {
            using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(path))
            {
                Assert.True(journal.Append(intent));
            }

            using (SqlitePortfolioOperationalStore portfolio =
                   SqlitePortfolioOperationalStore.Open(path))
            using (SqliteExchangeTestStore exchange = SqliteExchangeTestStore.Open(path,
                       metadata))
            {
                Assert.Equal(ReconciliationStatus.Acknowledged,
                    portfolio.AcknowledgeReconciliation(portfolioId, 0).Status);
                _ = portfolio.TryClaimDispatch(portfolioId, intent, 0,
                    new UtcTimestamp(Time(1)));
                ExecutionTransitionResult accepted = portfolio.ApplyExchangeTestObservation(
                    exchange, portfolioId, ExecutionObservation.Accepted("capture-accepted",
                        intent.IntentId, "42"), 0,
                    [Receipt(metadata.RunId, intent.IntentId, "capture-accepted-event", 1)]);
                Assert.Equal(ExecutionApplyDisposition.Applied, accepted.Disposition);
                ExecutionTransitionResult transition = portfolio.ApplyExchangeTestObservation(
                    exchange, portfolioId, ExecutionObservation.PartialFill("capture-observation",
                        intent.IntentId, "42", 0.5m), 0,
                    [Receipt(metadata.RunId, intent.IntentId, "capture-event", 2)],
                    fill: Fill(intent.IntentId),
                    checkpoint: new ReconciliationCheckpoint(portfolioId, 1,
                        [intent.IntentId], [], [], []));
                Assert.Equal(ExecutionApplyDisposition.Applied, transition.Disposition);

                ExchangeTestEvidence evidence = ExchangeTestEvidence.Capture(exchange, portfolio,
                    portfolioId, [intent.IntentId], new ExchangeTestEvidenceCaptureOptions
                    {
                        RestartVerified = true,
                        IdempotencyVerified = true
                    });
                Assert.Single(evidence.Lifecycles);
                Assert.Single(evidence.Fills);
                Assert.Collection(evidence.Events, _ => { }, _ => { });
                Assert.True(evidence.Recovery.RestartVerified);
            }

            using SqlitePortfolioOperationalStore reopenedPortfolio =
                SqlitePortfolioOperationalStore.Open(path);
            using SqliteExchangeTestStore reopenedExchange = SqliteExchangeTestStore.Open(path,
                metadata);
            ExchangeTestEvidence reopened = ExchangeTestEvidence.Capture(reopenedExchange,
                reopenedPortfolio, portfolioId, [intent.IntentId],
                new ExchangeTestEvidenceCaptureOptions
                {
                    RestartVerified = true,
                    IdempotencyVerified = true
                });

            Assert.Equal(reopened.CanonicalJson,
                ExchangeTestEvidence.Deserialize(reopened.CanonicalJson).CanonicalJson);
            Assert.Single(reopened.Fills);
            Assert.Equal("binance-trade:7", reopened.Fills[0].FillId);
        }
        finally
        {
            foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    File.Delete(candidate);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public void ManualPositionAndMissingRecoveryRemainExplicitBlockers()
    {
        PortfolioId portfolioId = new("manual-evidence-portfolio");
        Position position = new("manual-position", new InstrumentId("BTCUSDT"),
            Direction.Long, new Quantity(0.4m), new UtcTimestamp(Time(1)),
            new UtcTimestamp(Time(2)), new Price(100m));
        ExchangeTestPersistedSnapshot original = CreateSnapshot();
        ExchangeTestPersistedSnapshot snapshot = new(original.RunMetadata,
            original.PrivateEvents, original.Intents, original.Lifecycles, original.Attempts,
            original.ExecutionReceipts, original.Fills,
            [new PortfolioPositionSnapshot(portfolioId, position, null, PositionOrigin.Manual)],
            original.Checkpoints, original.Reservations, original.ProtectiveLinks);

        ExchangeTestEvidence evidence = ExchangeTestEvidence.Build(snapshot);

        Assert.Contains("manual-or-unknown-position", evidence.Blockers);
        Assert.Contains("restart-not-verified", evidence.Unknowns);
        Assert.False(evidence.IsComplete);
    }

    [Fact]
    public void EvidenceRejectsCredentialBearingProvenanceAndMismatchedRunRows()
    {
        Assert.Throws<ArgumentException>(() => ExchangeTestEvidence.Build(CreateSnapshot(),
            new ExchangeTestEvidenceCaptureOptions
            {
                ProvenanceDetails = ["apiSecret=fixture-only-value"]
            }));

        ExchangeTestPersistedSnapshot original = CreateSnapshot();
        ExchangeTestPersistedSnapshot mismatched = new(original.RunMetadata,
            [Receipt("other-run", "evidence-intent", "event-other", 1)], original.Intents,
            original.Lifecycles, original.Attempts, original.ExecutionReceipts,
            original.Fills, original.Positions, original.Checkpoints,
            original.Reservations, original.ProtectiveLinks);

        Assert.Throws<InvalidDataException>(() => ExchangeTestEvidence.Build(mismatched));
    }

    private static ExchangeTestPersistedSnapshot CreateSnapshot(
        string runId = "exchange-test-run")
    {
        ExchangeTestRunMetadata metadata = Metadata(runId);
        OrderIntent intent = CreateIntent("evidence-intent", "evidence-client");
        ExecutionStateSnapshot lifecycle = new(ExecutionStateSnapshot.CurrentFormatVersion,
            intent.IntentId, intent.ClientOrderId, intent.Quantity.Value, 0.5m, 1,
            ExecutionLifecycleState.PartiallyFilled, "42",
            new ImmutableValueList<ExecutionObservation>([
                ExecutionObservation.PartialFill("observation-1", intent.IntentId, "42", 0.5m)]),
            requiresReconciliation: true, expectedReconciliationRevision: 0);
        ExecutionDispatchAttempt attempt = new("attempt-1", intent.IntentId, 1, 0,
            new UtcTimestamp(Time(1)));
        ExchangeTestEventReceipt receipt = Receipt(metadata.RunId, intent.IntentId,
            "event-1", 1);
        PortfolioId portfolioId = new("evidence-portfolio");
        ReconciliationCheckpoint checkpoint = new(portfolioId, 1, [intent.IntentId], [],
            ["unknown-position"], []);
        PortfolioReservation reservation = new("reservation-1", portfolioId,
            intent.IntentId, new Money(1m, "USDT"), new Money(100m, "USDT"), 1);
        return new ExchangeTestPersistedSnapshot(metadata, [receipt], [intent], [lifecycle],
            [attempt], lifecycle.Receipts.ToArray(), [Fill(intent.IntentId)], [], [checkpoint],
            [new ExchangeTestPersistedReservation(reservation.ReservationId,
                portfolioId.Value, intent.IntentId, false, reservation)], []);
    }

    private static ExchangeTestRunMetadata Metadata(string runId) => new(runId,
        "evidence-config", "binance-usdm-test", Time(0));

    private static OrderIntent CreateIntent(string intentId, string clientOrderId) => new(
        intentId, "evidence-strategy", "evidence-setup", clientOrderId,
        new InstrumentId("BTCUSDT"), Direction.Long, OrderRole.Entry, 1,
        new Quantity(1m), new Price(100m));

    private static Fill Fill(string intentId) => new("binance-trade:7", intentId,
        new InstrumentId("BTCUSDT"), new Quantity(0.5m), new Price(100m),
        new UtcTimestamp(Time(1)), new UtcTimestamp(Time(2)), new UtcTimestamp(Time(3)), 1);

    private static ExchangeTestEventReceipt Receipt(string runId, string intentId,
        string identity, long sequence) => new(runId, identity, intentId, "ORDER_TRADE_UPDATE",
        sequence, Time(1), Time(2), Time(3), "42", 0.5m, "PARTIALLY_FILLED", false,
        new string('a', 64));

    private static DateTimeOffset Time(int seconds) =>
        new(2026, 10, 24, 12, 0, seconds, TimeSpan.Zero);
}
