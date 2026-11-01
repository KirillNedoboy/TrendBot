using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqliteExchangeTestStoreTests
{
    [Fact]
    public void PrivateEventReceiptsAreIsolatedAndIdempotentAcrossRestart()
    {
        using TemporaryDatabase database = new();
        ExchangeTestRunMetadata metadata = new("run-test-1", "profile-test-1",
            "binance-usdm-test", Time(0));
        ExchangeTestEventReceipt receipt = CreateReceipt(metadata.RunId, "event-1", 7);

        using (SqliteExchangeTestStore store = SqliteExchangeTestStore.Open(database.Path,
                   metadata))
        {
            Assert.Equal(ExchangeTestReceiptWriteDisposition.Recorded,
                store.AppendPrivateEvent(receipt));
            Assert.Equal(ExchangeTestReceiptWriteDisposition.AlreadyRecorded,
                store.AppendPrivateEvent(receipt));
            Assert.Single(store.ReadPrivateEvents());
        }

        using SqliteExchangeTestStore reopened = SqliteExchangeTestStore.Open(database.Path,
            metadata);
        Assert.Equal(metadata, reopened.ReadRunMetadata());
        Assert.Equal(receipt, Assert.Single(reopened.ReadPrivateEvents()));
    }

    [Fact]
    public void ConflictingReceiptIdentityFailsClosedWithoutRawPayloadStorage()
    {
        using TemporaryDatabase database = new();
        ExchangeTestRunMetadata metadata = new("run-test-2", "profile-test-1",
            "binance-usdm-test", Time(0));
        ExchangeTestEventReceipt receipt = CreateReceipt(metadata.RunId, "event-1", 7);
        using SqliteExchangeTestStore store = SqliteExchangeTestStore.Open(database.Path,
            metadata);

        _ = store.AppendPrivateEvent(receipt);
        ExchangeTestEventReceipt conflict = new(receipt.RunId, receipt.Identity,
            receipt.IntentId, receipt.EventType, receipt.StableSequence, receipt.EventTime,
            receipt.ReceiveTime, receipt.AvailabilityTime, receipt.ExchangeOrderId,
            receipt.FillQuantity, "FILLED", receipt.RequiresReconciliation,
            new string('a', 64));

        Assert.Throws<InvalidOperationException>(() => store.AppendPrivateEvent(conflict));
        Assert.DoesNotContain("raw", string.Join('|',
            store.ReadPrivateEvents().Select(static value => value.EventType)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static ExchangeTestEventReceipt CreateReceipt(string runId, string identity,
        long stableSequence) => new(runId, identity, "intent-test-1", "ORDER_TRADE_UPDATE",
        stableSequence, Time(1), Time(2), Time(3), "order-test-1", 0.4m, "PARTIALLY_FILLED",
        true, new string('b', 64));

    private static DateTimeOffset Time(int seconds) =>
        new(2026, 10, 24, 12, 0, seconds, TimeSpan.Zero);

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"tradingbot-exchange-test-{Guid.NewGuid():N}.sqlite");

        public string Path { get; }

        public void Dispose()
        {
            foreach (string candidate in new[] { Path, Path + "-wal", Path + "-shm" })
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
}
