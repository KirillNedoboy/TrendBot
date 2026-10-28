using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class OperationalShadowAuditExportTests
{
    [Fact]
    public async Task AuditBundleRestoresDeterministicallyAndRetainsHealthAndCausalEvidence()
    {
        using TemporaryShadowDatabase database = new();
        using OperationalShadowStore store = OperationalShadowStore.Open(database.Path);
        await using OperationalShadowRuntime runtime = CreateRuntime(store, "shadow");

        runtime.Process(CreateObservation("shadow", 1));

        OperationalShadowAuditEvidence first = OperationalShadowAuditEvidenceBuilder.Build(
            store.ReadAuditSnapshot(runtime.RunId));
        OperationalShadowAuditEvidence restored =
            OperationalShadowAuditEvidence.Deserialize(first.CanonicalJson);

        Assert.Equal(first.CanonicalJson, restored.CanonicalJson);
        Assert.Equal(first.Identity, restored.Identity);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(first.CanonicalJson))), first.ContentHash);
        Assert.True(restored.HealthSnapshots.Count >= 2);
        Assert.Contains(restored.HealthSnapshots, health => health.CausalCheckpointLink is not null);
        Assert.Single(restored.JournalEntries);
        Assert.Null(restored.JournalEntries[0].ExchangeTime);
        Assert.Contains("coverageKnown", restored.HealthSnapshots[0].CanonicalJson,
            StringComparison.Ordinal);
        Assert.All(restored.HealthSnapshots, health => Assert.Equal(0, health.RealOrderCount));
        Assert.All(restored.JournalEntries, entry => Assert.Equal(0, entry.RealOrderCount));
        Assert.All(restored.HealthSnapshots, health => Assert.Equal(
            OperationalShadowReconciliationStatus.NotApplicableInShadow,
            health.ReconciliationStatus));
        Assert.ThrowsAny<Exception>(() => OperationalShadowAuditEvidence.Deserialize(
            first.CanonicalJson.Replace(first.Identity, new string('0', 64),
                StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AuditSnapshotScopesJournalRowsToTheRequestedRunReferences()
    {
        using TemporaryShadowDatabase database = new();
        using OperationalShadowStore store = OperationalShadowStore.Open(database.Path);
        await using OperationalShadowRuntime firstRuntime = CreateRuntime(store, "shadow-a");
        await using OperationalShadowRuntime secondRuntime = CreateRuntime(store, "shadow-b");

        firstRuntime.Process(CreateObservation("shadow-a", 1));
        secondRuntime.Process(CreateObservation("shadow-b", 1));

        OperationalShadowAuditEvidence first = OperationalShadowAuditEvidenceBuilder.Build(
            store.ReadAuditSnapshot(firstRuntime.RunId));
        OperationalShadowAuditEvidence second = OperationalShadowAuditEvidenceBuilder.Build(
            store.ReadAuditSnapshot(secondRuntime.RunId));

        Assert.NotEqual(first.Identity, second.Identity);
        Assert.All(first.HealthSnapshots, health => Assert.Equal(first.RunId, health.RunId));
        Assert.Single(first.JournalEntries);
        Assert.Equal("shadow-a", first.JournalEntries[0].StreamId);
        Assert.DoesNotContain(first.JournalEntries, entry => entry.StreamId == "shadow-b");
        Assert.Equal("shadow-b", Assert.Single(second.JournalEntries).StreamId);
    }

    [Fact]
    public async Task AuditBuilderReportsMissingSequencesAndUnresolvedCausalLinksAsBlockers()
    {
        using TemporaryShadowDatabase database = new();
        using OperationalShadowStore store = OperationalShadowStore.Open(database.Path);
        await using OperationalShadowRuntime runtime = CreateRuntime(store, "shadow");
        runtime.Process(CreateObservation("shadow", 1));

        using (SqliteConnection connection = OpenRaw(database.Path))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE shadow_health_evidence
                SET health_sequence = 2,
                    causal_checkpoint_link = 'missing/99/missing',
                    health_json = replace(
                        replace(health_json, '"healthSequence":1', '"healthSequence":2'),
                        '"causalCheckpointLink":"shadow/1/trade-1-shadow"',
                        '"causalCheckpointLink":"missing/99/missing"')
                WHERE run_id = $run_id AND health_sequence = 1;
                """;
            command.Parameters.AddWithValue("$run_id", runtime.RunId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        OperationalShadowAuditEvidence evidence = OperationalShadowAuditEvidenceBuilder.Build(
            store.ReadAuditSnapshot(runtime.RunId));

        Assert.Contains("health-sequence-gap", evidence.Blockers);
        Assert.Contains("causal-checkpoint-unresolved", evidence.Blockers);
    }

    [Fact]
    public async Task AuditSnapshotRefusesUnsupportedEvidenceVersionAndCorruptMetadata()
    {
        using TemporaryShadowDatabase database = new();
        using OperationalShadowStore store = OperationalShadowStore.Open(database.Path);
        await using OperationalShadowRuntime runtime = CreateRuntime(store, "shadow");
        runtime.Process(CreateObservation("shadow", 1));

        using (SqliteConnection connection = OpenRaw(database.Path))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE shadow_health_evidence
                SET evidence_version = 99
                WHERE run_id = $run_id AND health_sequence = 0;
                """;
            command.Parameters.AddWithValue("$run_id", runtime.RunId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        Assert.Throws<InvalidDataException>(() => store.ReadAuditSnapshot(runtime.RunId));

        using (SqliteConnection connection = OpenRaw(database.Path))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE shadow_health_evidence
                SET evidence_version = 1,
                    health_json = '{}'
                WHERE run_id = $run_id AND health_sequence = 0;
                """;
            command.Parameters.AddWithValue("$run_id", runtime.RunId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        Assert.Throws<InvalidDataException>(() => store.ReadAuditSnapshot(runtime.RunId));
    }

    [Fact]
    public async Task AuditBuilderRefusesNonzeroRealOrders()
    {
        using TemporaryShadowDatabase database = new();
        using OperationalShadowStore store = OperationalShadowStore.Open(database.Path);
        await using OperationalShadowRuntime runtime = CreateRuntime(store, "shadow");
        runtime.Process(CreateObservation("shadow", 1));

        using (SqliteConnection connection = OpenRaw(database.Path))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE shadow_entries
                SET outcome_json = replace(outcome_json, '"realOrderCount":0',
                    '"realOrderCount":1')
                WHERE stream_id = 'shadow' AND stable_sequence = 1;
                """;
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        Assert.Throws<InvalidDataException>(() => OperationalShadowAuditEvidenceBuilder.Build(
            store.ReadAuditSnapshot(runtime.RunId)));
    }

    [Fact]
    public async Task AuditExportIsAtomicUtf8IdempotentAndRefusesConflictsAndSidecars()
    {
        using TemporaryShadowDatabase database = new();
        using OperationalShadowStore store = OperationalShadowStore.Open(database.Path);
        await using OperationalShadowRuntime runtime = CreateRuntime(store, "shadow");
        runtime.Process(CreateObservation("shadow", 1));
        OperationalShadowAuditEvidence evidence = OperationalShadowAuditEvidenceBuilder.Build(
            store.ReadAuditSnapshot(runtime.RunId));
        string path = Path.Combine(database.DirectoryPath, "audit.json");

        Assert.True(OperationalShadowAuditExporter.Export(evidence, path));
        Assert.False(OperationalShadowAuditExporter.Export(evidence, path));
        Assert.Equal(evidence.CanonicalJson, File.ReadAllText(path, new UTF8Encoding(false)));
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);
        File.WriteAllText(path, "conflict");
        Assert.Throws<InvalidOperationException>(() =>
            OperationalShadowAuditExporter.Export(evidence, path));
        Assert.Equal("conflict", File.ReadAllText(path));

        Assert.Throws<InvalidOperationException>(() =>
            OperationalShadowAuditExporter.Export(evidence, database.Path));
        Assert.Throws<InvalidOperationException>(() =>
            OperationalShadowAuditExporter.Export(evidence, database.Path + "-wal"));
        Assert.Throws<InvalidOperationException>(() =>
            OperationalShadowAuditExporter.Export(evidence, database.Path + "-shm"));
    }

    [Fact]
    public async Task PromotionSnapshotHasOperationalCategoryBlockersAndNoExecutionAuthority()
    {
        using TemporaryShadowDatabase database = new();
        using OperationalShadowStore store = OperationalShadowStore.Open(database.Path);
        await using OperationalShadowRuntime runtime = CreateRuntime(store, "shadow");
        runtime.Process(CreateObservation("shadow", 1));
        OperationalShadowAuditEvidence evidence = OperationalShadowAuditEvidenceBuilder.Build(
            store.ReadAuditSnapshot(runtime.RunId));

        PromotionEvidenceSnapshot snapshot = evidence.ToPromotionEvidenceSnapshot();

        Assert.Equal(PromotionEvidenceCategory.OperationalShadow, snapshot.Category);
        Assert.Equal(PromotionEvidenceStatus.Provided, snapshot.Status);
        Assert.Contains("execution-authority-not-granted", snapshot.Blockers);
        Assert.Contains("operational-shadow-store", snapshot.Provenance);
        Assert.Contains(runtime.RunId, snapshot.ReferencedIdentities);
        Assert.Equal(evidence.CanonicalJson, snapshot.CanonicalJson);
        Assert.DoesNotContain("PrivateDispatcher", typeof(OperationalShadowAuditEvidence)
            .GetMembers().Select(member => member.Name));
    }

    [Fact]
    public async Task AuditExportCommandReturnsTheContractedExitCodes()
    {
        using TemporaryShadowDatabase database = new();
        using OperationalShadowStore store = OperationalShadowStore.Open(database.Path);
        await using OperationalShadowRuntime runtime = CreateRuntime(store, "shadow");
        runtime.Process(CreateObservation("shadow", 1));
        string outputPath = Path.Combine(database.DirectoryPath, "audit.json");

        int success = await OperationalShadowAuditCommand.RunAsync(
            ["--database", database.Path, "--run-id", runtime.RunId,
             "--output", outputPath], new StringWriter(), TestContext.Current.CancellationToken);
        int badArguments = await OperationalShadowAuditCommand.RunAsync(
            ["--database", database.Path], new StringWriter(), TestContext.Current.CancellationToken);
        int exportError = await OperationalShadowAuditCommand.RunAsync(
            ["--database", database.Path, "--run-id", "missing-run",
             "--output", Path.Combine(database.DirectoryPath, "missing.json")],
            new StringWriter(), TestContext.Current.CancellationToken);

        Assert.Equal(0, success);
        Assert.Equal(2, badArguments);
        Assert.Equal(1, exportError);
        Assert.True(File.Exists(outputPath));
    }

    private static OperationalShadowRuntime CreateRuntime(OperationalShadowStore store,
        string streamId) => new(new DelegateOperationalShadowMarketDataSource(_ => Empty()), store,
        new OperationalShadowRuntimeOptions { StreamId = streamId });

    private static OperationalShadowMarketObservation CreateObservation(string streamId,
        long stableSequence)
    {
        UtcTimestamp time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture));
        Trade trade = new("trade-1-" + streamId, new InstrumentId("BTCUSDT"), time,
            new Price(100m), new Quantity(1m), TradeSide.Buy);
        CausalMarketEvent causal = new(trade, time, time, stableSequence);
        return new OperationalShadowMarketObservation(streamId, causal, dataFresh: false,
            reconnectReady: false, recoveryComplete: false, killSwitchEnabled: false,
            killSwitchKnown: true);
    }

    private static async IAsyncEnumerable<OperationalShadowMarketObservation> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static SqliteConnection OpenRaw(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TemporaryShadowDatabase : IDisposable
    {
        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "shadow-audit-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(DirectoryPath, "shadow.sqlite");

        public TemporaryShadowDatabase() => Directory.CreateDirectory(DirectoryPath);

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
