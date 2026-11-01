using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class LocalPaperStoreTests
{
    [Fact]
    public void StoreReopensWithSameStateAndRejectsConflictingIdentity()
    {
        string path = Path.Combine(Path.GetTempPath(), $"local-paper-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (LocalPaperStore store = LocalPaperStore.Open(path))
            {
                Assert.True(store.Append("stream", 1, "event", "payload", "state"));
                Assert.False(store.Append("stream", 1, "event", "payload", "state"));
            }

            using LocalPaperStore reopened = LocalPaperStore.Open(path);
            Assert.Equal("state", reopened.ReadCheckpoint("stream"));
            Action appendConflict = () => reopened.Append(
                "stream", 1, "event", "different", "state");
            Assert.Throws<InvalidOperationException>(appendConflict);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
            if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
        }
    }
}
