using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingBot.App;
using TradingBot.Backtesting;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class SignalOpportunityReportCommandTests
{
    [Theory]
    [InlineData(false, true, false, false, 0)]
    [InlineData(false, false, false, false, 0)]
    [InlineData(true, true, false, false, 0)]
    [InlineData(true, false, false, false, 0)]
    [InlineData(true, true, true, false, 0)]
    [InlineData(false, true, false, true, 0)]
    [InlineData(false, true, false, false, 1)]
    [InlineData(false, false, false, false, 1)]
    [InlineData(false, true, false, false, 2)]
    public void CommittedDecisionsMustMatchCapturedRevisions(bool withSignal, bool sameRevision,
        bool mismatchedSequence, bool mismatchedFreeze, int scenarioDiagnostics)
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"tradingbot-opportunity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const string streamId = "binance-usdm-native-kline:BTCUSDT";
            const string eventId = "bar-fixture";
            string revision = new('a', 64);
            string inputHash = sameRevision ? revision : new string('b', 64);
            DateTimeOffset open = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            UtcTimestamp close = new(open.AddMinutes(15));
            string ledgerPath = Path.Combine(directory, "bars.sqlite");
            string journalPath = Path.Combine(directory, "signals.sqlite");
            string capturePath = Path.Combine(directory, "capture.json");
            string profilePath = Path.Combine(directory, "profile.json");
            string declarationPath = Path.Combine(directory, "declaration.json");
            string freezePath = Path.Combine(directory, "frozen-journal.json");
            string manifestPath = Path.Combine(directory, "manifest.json");
            string profileJson = CoreJsonSerializer.Serialize(CreateProfile(open));
            File.WriteAllText(profilePath, profileJson);
            string configurationHash = Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(profileJson))).ToLowerInvariant();
            string payload = JsonSerializer.Serialize(new
            {
                kline = new { timeframe = "15m", openTimeMilliseconds = open.ToUnixTimeMilliseconds() },
                sourceMetadata = new { availabilityTime = new { value = close.Value }, stableSequence = 1 }
            });
            using (SqliteNativeBarInputLedger ledger = SqliteNativeBarInputLedger.Open(ledgerPath))
            {
                Assert.True(ledger.Append(streamId, new NativeBarInputLedgerReceipt(1, eventId,
                    revision, NativeBarInputReceiptStatus.Accepted, payload, null, null,
                    eventId, close)));
                string contextPayload = JsonSerializer.Serialize(new
                {
                    kline = new { timeframe = "1h", openTimeMilliseconds = open.AddHours(-1).ToUnixTimeMilliseconds() },
                    sourceMetadata = new { availabilityTime = new { value = open }, stableSequence = 2 }
                });
                Assert.True(ledger.Append(streamId, new NativeBarInputLedgerReceipt(2, "context-fixture",
                    new string('e', 64), NativeBarInputReceiptStatus.Accepted, contextPayload,
                    null, null, null, null)));
            }

            SignalScenarioPolicySet policies = new(1,
                new SignalScenarioExpiryPolicy(1, "fixture-expiry", TimeSpan.FromHours(1)),
                new SignalScenarioRearmPolicy(1, "fixture-rearm", TimeSpan.Zero, true),
                new SignalScenarioInvalidationPolicy(1, "fixture-invalidation", true, true));
            SignalScenarioProjectionEngine engine = new(policies);
            SignalScenarioProjectionSnapshot snapshot = engine.CreateInitialSnapshot();
            using (SqliteSignalJournalStore journal = SqliteSignalJournalStore.Open(journalPath))
            {
                if (withSignal)
                    AppendFixtureSignal(journal, engine, snapshot, streamId, eventId, revision,
                        sameRevision ? new string('e', 64) : new string('f', 64), configurationHash, open,
                        mismatchedSequence ? 1 : 2);
                else
                {
                    string diagnosticEventId = scenarioDiagnostics > 0
                        ? $"scenario-confirm:{eventId}:CenterRetest:Long" : eventId;
                    SignalJournalDiagnostic diagnostic = SignalJournalDiagnostic.Create(streamId,
                        diagnosticEventId, inputHash, scenarioDiagnostics > 0
                            ? "confirmed-setup-anchor-unavailable" : "no-confirmed-setup", ["fixture-reason"]);
                    Assert.True(journal.Append(new SignalJournalCommit(streamId, 0, 1, diagnosticEventId,
                        inputHash, close, close, 1, null, snapshot, [], diagnostic: diagnostic)));
                    if (scenarioDiagnostics == 2)
                    {
                        string secondId = $"scenario-confirm:{eventId}:BoundaryRejection:Short";
                        SignalJournalDiagnostic second = SignalJournalDiagnostic.Create(streamId,
                            secondId, inputHash, "scenario-transition-rejected", ["second-fixture-reason"]);
                        Assert.True(journal.Append(new SignalJournalCommit(streamId, 1, 2, secondId,
                            inputHash, close, close, 2, null, snapshot, [], diagnostic: second)));
                    }
                }
            }

            string universeHash = new('c', 64);
            string captureConfigurationHash = new('d', 64);
            File.WriteAllText(capturePath, JsonSerializer.Serialize(new
            {
                report = new
                {
                    succeeded = true,
                    runId = "capture-fixture",
                    configurationSha256 = captureConfigurationHash,
                    pointInTimeUniverse = new { eligible = true, symbol = "BTCUSDT", snapshotSha256 = universeHash },
                    coverage = new[]
                    {
                        new
                        {
                            timeframe = "15m", intervalMilliseconds = 900000,
                            expectedOpenTimeMilliseconds = new[] { open.ToUnixTimeMilliseconds() },
                            coveredOpenTimeMilliseconds = new[] { open.ToUnixTimeMilliseconds() },
                            availabilityModel = "fixture"
                        }
                    }
                }
            }));
            string datasetHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ledgerPath)))
                .ToLowerInvariant();
            SignalStudyManifest manifest = SignalStudyManifest.Create("capture-fixture",
                Enum.GetValues<SignalStudyInputKind>().Select(kind => kind switch
                {
                    SignalStudyInputKind.Dataset => SignalStudyHashDeclaration.Declared(kind, datasetHash),
                    SignalStudyInputKind.PointInTimeUniverse => SignalStudyHashDeclaration.Declared(kind, universeHash),
                    SignalStudyInputKind.Configuration => SignalStudyHashDeclaration.Declared(kind, configurationHash),
                    _ => SignalStudyHashDeclaration.Unknown(kind, "fixture-not-declared")
                }),
                Enum.GetValues<SignalStudyCriterionKind>().Select(kind =>
                    SignalStudyCriterionDeclaration.Unknown(kind, "fixture-not-declared")),
                SignalStudyTemporalSplitPlan.Unknown("fixture-not-declared"),
                SignalStudyTrialRegistry.Unknown("fixture-not-declared"));
            SignalStudyManifestJournal.WriteOnce(manifest, manifestPath);
            string policyJson = CoreJsonSerializer.Serialize(policies);
            string policyHash = Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(policyJson))).ToLowerInvariant();
            string captureHash = FileHash(capturePath);
            File.WriteAllText(declarationPath, JsonSerializer.Serialize(new
            {
                formatVersion = "t017-research-freeze-v1",
                datasetHash,
                coverageHash = captureHash,
                profileHash = configurationHash,
                scenarioPolicyHash = policyHash
            }));
            File.WriteAllText(freezePath, JsonSerializer.Serialize(new
            {
                formatVersion = "t017-journal-freeze-v1",
                predeclaredRunSha256 = FileHash(declarationPath),
                studyManifestSha256 = FileHash(manifestPath),
                datasetSha256 = datasetHash,
                coverageSha256 = captureHash,
                profileSha256 = configurationHash,
                scenarioPolicySha256 = policyHash,
                signalJournalSha256 = mismatchedFreeze ? new string('f', 64) : FileHash(journalPath)
            }));
            using StringWriter output = new(CultureInfo.InvariantCulture);
            using StringWriter error = new(CultureInfo.InvariantCulture);
            int exitCode = SignalOpportunityReportCommand.Run(
                ["signal-only", "opportunity-report", "--capture", capturePath,
                    "--ledger", ledgerPath, "--manifest", manifestPath,
                    "--journal", journalPath, "--symbol", "BTCUSDT", "--profile", profilePath,
                    "--declaration", declarationPath, "--freeze", freezePath], output, error);
            if (sameRevision && !mismatchedSequence && !mismatchedFreeze)
            {
                Assert.Equal(0, exitCode);
                using JsonDocument report = JsonDocument.Parse(output.ToString());
                Assert.Equal(withSignal || scenarioDiagnostics > 0 ? 0 : 1, report.RootElement.GetProperty("denominators")
                    .GetProperty("noSignal").GetInt32());
                Assert.Equal(scenarioDiagnostics > 0 ? 1 : 0, report.RootElement.GetProperty("denominators")
                    .GetProperty("blocked").GetInt32());
                Assert.Equal(withSignal ? 1 : 0, report.RootElement.GetProperty("denominators")
                    .GetProperty("uniqueSignals").GetInt32());
                Assert.Equal(configurationHash, report.RootElement.GetProperty("configurationHash").GetString());
                Assert.Equal(captureConfigurationHash, report.RootElement.GetProperty("captureConfigurationHash").GetString());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(journalPath))).ToLowerInvariant(),
                    report.RootElement.GetProperty("signalJournalHash").GetString());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(policyJson))).ToLowerInvariant(),
                    report.RootElement.GetProperty("scenarioPolicyHash").GetString());
                if (!withSignal)
                {
                    string[] reasons = report.RootElement.GetProperty("rows")[0]
                        .GetProperty("reasonCodes").EnumerateArray().Select(static item => item.GetString()!).ToArray();
                    Assert.Contains("fixture-reason", reasons);
                    if (scenarioDiagnostics == 2)
                    {
                        Assert.Contains("second-fixture-reason", reasons);
                        Assert.Contains("scenario-transition-rejected", reasons);
                    }
                }
                Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(capturePath))).ToLowerInvariant(),
                    report.RootElement.GetProperty("captureReportHash").GetString());
                Assert.Equal(FileHash(declarationPath), report.RootElement.GetProperty("replayDeclarationHash").GetString());
                Assert.Equal(FileHash(freezePath), report.RootElement.GetProperty("journalFreezeHash").GetString());
            }
            else
            {
                Assert.Equal(1, exitCode);
                Assert.Empty(output.ToString());
                Assert.Contains("InvalidDataException", error.ToString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string FileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void AppendFixtureSignal(SqliteSignalJournalStore journal,
        SignalScenarioProjectionEngine engine, SignalScenarioProjectionSnapshot snapshot,
        string streamId, string eventId, string revision, string contextRevision,
        string profileHash, DateTimeOffset open, long contextSequence)
    {
        UtcTimestamp openTime = new(open);
        UtcTimestamp closeTime = new(open.AddMinutes(15));
        SignalScenarioAnchor anchor = SignalScenarioAnchor.Create(new InstrumentId("BTCUSDT"),
            "BINANCE_USDM", "15m", Direction.Long, SetupType.CenterRetest,
            "fixture-v1", "center-retest-v1", eventId, openTime, 1);
        SignalScenarioInput input = SignalScenarioInput.Confirm(streamId, "confirm-fixture",
            anchor, closeTime, closeTime, 3);
        SignalScenarioProjectionTransition transition = engine.Apply(snapshot, input);
        Assert.Equal(SignalScenarioTransitionStatus.Applied, transition.Status);
        SignalInputReference setup = new("public", "source-v1", eventId, revision,
            SignalInputRole.Setup, "15m", closeTime, closeTime, 1);
        SignalInputReference context = new("public", "source-v1", "context-fixture", contextRevision,
            SignalInputRole.HigherTimeframe, "1h", openTime, openTime, contextSequence);
        SignalProvenance provenance = SignalProvenance.Create("fixture-v1", profileHash,
            new CausalEventCursor(closeTime, 3), new CausalEventCursor(openTime, 2), [setup, context]);
        SignalEvidenceRecord evidence = SignalEvidenceRecord.Create(anchor.Identity,
            provenance, SignalEvidenceState.Complete, ["fixture-reason"]);
        SignalRecord signal = SignalRecord.Create(anchor.Identity, evidence,
            SignalAssessmentState.ConfirmedOnClosedBar, new SignalBarInterval(openTime, closeTime),
            closeTime, new SignalReferencePrice("public", "source-v1", eventId, closeTime, new Price(100m)));
        Assert.True(journal.Append(new SignalJournalCommit(streamId, 0, 1, input.EventId,
            revision, closeTime, closeTime, 3, input.InputHash, transition.NextSnapshot,
            transition.EmittedEvents, signal, evidence)));
    }

    private static PublicSignalProfileConfiguration CreateProfile(DateTimeOffset start)
    {
        PublicSignalTimeframeConfiguration Timeframe(string timeframe) => new(timeframe,
            PublicSignalDataSource.BinanceUsdMNativeKlines, "source-v1", start, "history-v1",
            1, "seed-v1", 1, "warmup-v1", "freshness-v1",
            TimeSpan.FromHours(2), TimeSpan.FromHours(2));
        PublicSignalIndicatorConfiguration indicators = new(
            priceContextVersion: "price-context-v2", featureQualityVersion: "feature-quality-v1",
            setupFamilyVersion: "lane-a-v1", pivotLeftBars: 1, pivotRightBars: 1,
            atrPeriodBars: 1, atrMode: PriceContextAtrMode.Sma,
            atrValueUnit: PublicSignalValueUnit.Price,
            channelCenterKind: PriceContextChannelCenterKind.Ema, channelCenterPeriodBars: 1,
            hlc3SmoothingKind: PriceContextMovingAverageKind.Ema,
            channelCenterValueUnit: PublicSignalValueUnit.Price,
            channelWidthKind: PriceContextChannelWidthKind.Atr, channelWidthPeriodBars: 1,
            channelWidthMultiplier: 1m, channelWidthValueUnit: PublicSignalValueUnit.Price,
            channelWidthMultiplierUnit: PublicSignalMultiplierUnit.Dimensionless,
            levelBufferMultiplier: 1m, levelBufferMultiplierUnit: PublicSignalMultiplierUnit.Dimensionless,
            htfFastPeriodBars: 1, htfSlowPeriodBars: 1,
            htfMovingAverageKind: PriceContextMovingAverageKind.Ema, htfConfirmationBars: 1,
            htfMovingAverageValueUnit: PublicSignalValueUnit.Price,
            boundaryMinimumChannelWidth: 1m, boundaryMinimumChannelWidthUnit: PublicSignalValueUnit.Price);
        return new PublicSignalProfileConfiguration(1, "fixture-v1", Timeframe("15m"),
            Timeframe("1h"), indicators, new PublicSignalUniverseConfiguration("fixture-v1", 1, 1));
    }
}
