using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqliteFeatureQualitySnapshotStoreTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MetricsPublisherExposesReadinessReasonsAgesAndUnknownCoverage()
    {
        FeatureQualitySnapshot snapshot = CreateSnapshot();
        using MeterListener listener = new();
        List<(string Name, double Value)> measurements = [];
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == FeatureQualityMetricsPublisher.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            measurements.Add((instrument.Name, value)));
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            measurements.Add((instrument.Name, value)));
        listener.Start();

        using FeatureQualityMetricsPublisher publisher = new();
        publisher.Publish(snapshot.Quality);
        FeatureQualityEvidence unknownEvidence = new(
            FeatureQualityFeatureKind.RollingAtr, FeatureQualityInputSeries.Rolling,
            "rolling-v1", "UnknownWarmup", "Incomplete", FeatureQualityFreshness.Unknown,
            FeatureQualityFreshness.Unknown, null, null, null,
            FeatureQualityCoverage.Unknown, null, 0, 0, false,
            new ImmutableValueList<string>(["ttl-missing:rolling"]));
        FeatureQualityEvaluation unknown = new(
            FeatureQualityEvaluation.CurrentAlgorithmVersion, snapshot.Cursor,
            new FeatureQualityPolicy(), "rolling-v1", null,
            new ImmutableValueList<FeatureQualityEvidence>([unknownEvidence]));
        publisher.Publish(unknown);
        listener.RecordObservableInstruments();

        Assert.Contains(measurements, item => item.Name == "feature_quality.ready_count");
        Assert.Contains(measurements, item => item.Name == "feature_quality.reason_count");
        Assert.Contains(measurements,
            item => item.Name == "feature_quality.market_data_age_ms");
        Assert.Contains(measurements,
            item => item.Name == "feature_quality.availability_age_ms");
        Assert.Contains(measurements, item => item.Name == "feature_quality.known_coverage");
        Assert.Contains(measurements, item => item.Name == "feature_quality.coverage_known"
            && item.Value == 0d);
        Assert.Contains(measurements, item => item.Name == "feature_quality.known_coverage"
            && item.Value == -1d);
        Assert.Contains(measurements, item => item.Name == "feature_quality.market_data_age_ms"
            && item.Value == -1d);
        Assert.Contains(measurements, item => item.Name == "feature_quality.availability_age_ms"
            && item.Value == -1d);
        Assert.Contains(measurements, item => item.Name == "feature_quality.evaluations");
    }

    [Fact]
    public void AppendFindRetryReopenAndMigrateSchemaEightPreserveRows()
    {
        using TemporaryDatabase database = new();
        FeatureQualitySnapshot snapshot = CreateSnapshot();
        using (SqliteEventJournal journal = SqliteEventJournal.Open(database.Path))
        {
            Assert.True(journal.Append("stream-a", new CausalMarketEvent(
                new Trade("trade-before-quality", Instrument, Time(0), new Price(100m),
                    new Quantity(1m), TradeSide.Buy), Time(0), Time(0), 0)));
        }

        using (SqliteFeatureQualitySnapshotStore store =
            SqliteFeatureQualitySnapshotStore.Open(database.Path))
        {
            Assert.True(store.Append(snapshot));
            Assert.False(store.Append(snapshot));
            Assert.Equal(snapshot, store.Find(snapshot.SnapshotId));
            Assert.Equal(snapshot, store.Find(snapshot.IdentityBytes));
        }

        using (SqliteFeatureQualitySnapshotStore reopened =
            SqliteFeatureQualitySnapshotStore.Open(database.Path))
        {
            Assert.Equal(snapshot, reopened.Find(snapshot.SnapshotId));
        }

        using (SqliteConnection downgrade = OpenRaw(database.Path))
        {
            ExecuteNonQuery(downgrade, "DROP TABLE feature_quality_snapshots;");
            ExecuteNonQuery(downgrade, "PRAGMA user_version = 8;");
        }

        using (SqliteFeatureQualitySnapshotStore migrated =
            SqliteFeatureQualitySnapshotStore.Open(database.Path))
        {
            Assert.True(migrated.Append(snapshot));
        }

        using SqliteConnection verify = OpenRaw(database.Path);
        Assert.Equal(10L, ExecuteScalar<long>(verify, "PRAGMA user_version;"));
        Assert.Equal(1L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM event_journal WHERE stream_id = 'stream-a';"));
        Assert.Equal(1L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM feature_quality_snapshots;"));
    }

    [Fact]
    public void CorruptRowsUnsupportedVersionsAndWrongSchemaAreRejected()
    {
        using TemporaryDatabase nonCanonicalDatabase = new();
        FeatureQualitySnapshot snapshot = CreateSnapshot();
        using (SqliteFeatureQualitySnapshotStore store =
            SqliteFeatureQualitySnapshotStore.Open(nonCanonicalDatabase.Path))
        {
            Assert.True(store.Append(snapshot));
            UpdateSnapshotJson(nonCanonicalDatabase.Path, snapshot.SnapshotId,
                CoreJsonSerializer.Serialize(snapshot) + " ");
            Assert.Throws<InvalidDataException>(() => store.Find(snapshot.SnapshotId));
        }

        using TemporaryDatabase unsupportedDatabase = new();
        using (SqliteFeatureQualitySnapshotStore store =
            SqliteFeatureQualitySnapshotStore.Open(unsupportedDatabase.Path))
        {
            Assert.True(store.Append(snapshot));
            string unsupported = CoreJsonSerializer.Serialize(snapshot).Replace(
                "\"formatVersion\":1", "\"formatVersion\":2",
                StringComparison.Ordinal);
            UpdateSnapshotJson(unsupportedDatabase.Path, snapshot.SnapshotId, unsupported,
                formatVersion: 2);
            Assert.Throws<InvalidDataException>(() => store.Find(snapshot.SnapshotId));
        }

        using TemporaryDatabase algorithmDatabase = new();
        using (SqliteFeatureQualitySnapshotStore store =
            SqliteFeatureQualitySnapshotStore.Open(algorithmDatabase.Path))
        {
            Assert.True(store.Append(snapshot));
            UpdateAlgorithmVersion(algorithmDatabase.Path, snapshot.SnapshotId,
                "feature-quality-v999");
            Assert.Throws<InvalidDataException>(() => store.Find(snapshot.SnapshotId));
        }

        using TemporaryDatabase schemaDatabase = new();
        using (SqliteFeatureQualitySnapshotStore store =
            SqliteFeatureQualitySnapshotStore.Open(schemaDatabase.Path))
        {
        }
        using (SqliteConnection connection = OpenRaw(schemaDatabase.Path))
        {
            ExecuteNonQuery(connection, "DROP TABLE feature_quality_snapshots;");
            ExecuteNonQuery(connection, """
                CREATE TABLE feature_quality_snapshots
                (
                    snapshot_id TEXT NOT NULL PRIMARY KEY,
                    format_version INTEGER NOT NULL,
                    algorithm_version TEXT NOT NULL,
                    snapshot_json TEXT NOT NULL
                );
                """);
        }

        Assert.Throws<InvalidDataException>(() =>
            SqliteFeatureQualitySnapshotStore.Open(schemaDatabase.Path));
    }

    [Fact]
    public void RecomputedQualityMismatchAndHeldWriterLeaveNoPartialRow()
    {
        using TemporaryDatabase database = new();
        FeatureQualitySnapshot snapshot = CreateSnapshot();
        using SqliteFeatureQualitySnapshotStore store =
            SqliteFeatureQualitySnapshotStore.Open(database.Path, 1);
        Assert.True(store.Append(snapshot));

        string canonical = CoreJsonSerializer.Serialize(snapshot);
        string altered = canonical.Contains("\"isReady\":true",
            StringComparison.Ordinal)
            ? canonical.Replace("\"isReady\":true", "\"isReady\":false",
                StringComparison.Ordinal)
            : canonical.Replace("\"isReady\":false", "\"isReady\":true",
                StringComparison.Ordinal);
        Assert.NotEqual(canonical, altered);
        byte[] alteredIdentity = SHA256.HashData(Encoding.UTF8.GetBytes(altered));
        UpdateIdentityAndJson(database.Path, snapshot.IdentityBytes, alteredIdentity, altered);
        Assert.Throws<InvalidDataException>(() => store.Find(alteredIdentity));

        using TemporaryDatabase lockedDatabase = new();
        FeatureQualitySnapshot lockedSnapshot = CreateSnapshot();
        using SqliteFeatureQualitySnapshotStore lockedStore =
            SqliteFeatureQualitySnapshotStore.Open(lockedDatabase.Path, 1);
        using SqliteConnection blocker = OpenRaw(lockedDatabase.Path);
        ExecuteNonQuery(blocker, "BEGIN IMMEDIATE;");
        Assert.Throws<SqliteException>(() => lockedStore.Append(lockedSnapshot));
        ExecuteNonQuery(blocker, "ROLLBACK;");
        Assert.Null(lockedStore.Find(lockedSnapshot.SnapshotId));
        Assert.True(lockedStore.Append(lockedSnapshot));
    }

    private static FeatureQualitySnapshot CreateSnapshot()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 101m, 99m, 10m),
            Bar(1, 101m, 102m, 100m, 20m),
            Bar(2, 102m, 103m, 101m, 30m),
            Bar(3, 103m, 104m, 102m, 40m)
        ];
        CausalEventCursor cursor = new(Time(4), 4);
        RollingIndicatorResult result = RollingIndicatorCalculator.Calculate(
            new RollingIndicatorInput("source", Instrument, "1m", Time(-1), bars), cursor,
            new RollingIndicatorConfiguration("1m",
                new RollingIndicatorVersion("rolling-v1"), 2, RollingAtrMode.Sma, 2,
                RollingMovingAverageKind.Sma, 2));
        return FeatureQualitySnapshot.Create(result,
            new FeatureQualityPolicy(TimeSpan.FromMinutes(1), null, null, null));
    }

    private static ScreenerBar Bar(int offset, decimal close, decimal high, decimal low,
        decimal volume)
    {
        UtcTimestamp eventTime = Time(offset);
        Bar bar = new($"bar-{offset}", Instrument, eventTime,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)), eventTime,
            new Price(close - 1m), new Price(high), new Price(low), new Price(close),
            new Quantity(volume));
        return new ScreenerBar("source", bar, volume * 10m,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)), eventTime, offset);
    }

    private static UtcTimestamp Time(int offset) =>
        new(BaseTime.AddMinutes(offset));

    private static SqliteConnection OpenRaw(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void UpdateSnapshotJson(string path, string snapshotId, string json,
        int? formatVersion = null)
    {
        using SqliteConnection connection = OpenRaw(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = formatVersion is null
            ? "UPDATE feature_quality_snapshots SET snapshot_json = $json "
                + "WHERE snapshot_id = $snapshot_id;"
            : "UPDATE feature_quality_snapshots SET snapshot_json = $json, "
                + "format_version = $format_version WHERE snapshot_id = $snapshot_id;";
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$snapshot_id", Convert.FromHexString(snapshotId));
        if (formatVersion is not null)
        {
            command.Parameters.AddWithValue("$format_version", formatVersion.Value);
        }

        command.ExecuteNonQuery();
    }

    private static void UpdateAlgorithmVersion(string path, string snapshotId,
        string algorithmVersion)
    {
        using SqliteConnection connection = OpenRaw(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE feature_quality_snapshots SET algorithm_version = "
            + "$algorithm_version WHERE snapshot_id = $snapshot_id;";
        command.Parameters.AddWithValue("$algorithm_version", algorithmVersion);
        command.Parameters.AddWithValue("$snapshot_id", Convert.FromHexString(snapshotId));
        command.ExecuteNonQuery();
    }

    private static void UpdateIdentityAndJson(string path, byte[] oldIdentity,
        byte[] newIdentity, string json)
    {
        using SqliteConnection connection = OpenRaw(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE feature_quality_snapshots SET snapshot_id = $new_id, "
            + "snapshot_json = $json WHERE snapshot_id = $old_id;";
        command.Parameters.AddWithValue("$new_id", newIdentity);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$old_id", oldIdentity);
        command.ExecuteNonQuery();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"tradingbot-feature-quality-{Guid.NewGuid():N}.sqlite");
        }

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
}
