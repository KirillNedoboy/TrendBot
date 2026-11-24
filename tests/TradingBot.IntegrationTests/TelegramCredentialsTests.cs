using TradingBot.App;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class TelegramCredentialsTests
{
    [Fact]
    public void MountedPairIsAtomicAndMalformedFilesNeverFallBackOrExposeContents()
    {
        string directory = Path.Combine(Path.GetTempPath(), "telegram-credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string tokenPath = Path.Combine(directory, "telegram-bot-token");
        string chatPath = Path.Combine(directory, "telegram-chat-id");
        try
        {
            File.WriteAllText(tokenPath, "12345:synthetic-mounted-token");
            File.WriteAllText(chatPath, "-100123456");
            TelegramCredentials pair = TelegramCredentials.Load(directory, "123:synthetic-environment-token", "987654");
            Assert.Equal("12345:synthetic-mounted-token", pair.Token);
            Assert.Equal("-100123456", pair.ChatId);
            File.Delete(chatPath);
            Assert.Throws<InvalidOperationException>(() => TelegramCredentials.Load(directory, "123:synthetic-environment-token", "987654"));
            foreach (string malformed in new[] { "contains a space", "contains\na-newline", "non-ascii-\u2603", new string('x', 257) })
            {
                File.WriteAllText(tokenPath, malformed);
                File.WriteAllText(chatPath, "-100123456");
                Exception failure = Assert.Throws<InvalidOperationException>(() => TelegramCredentials.Load(directory, "123:synthetic-environment-token", "987654"));
                Assert.DoesNotContain(malformed, failure.ToString());
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(null, "123456")]
    [InlineData("synthetic-token", null)]
    [InlineData("has a space", "123456")]
    [InlineData("synthetic-token", "123\n456")]
    public void MissingOrUnsafeEnvironmentPairFailsClosed(string? token, string? chatId)
    {
        Assert.Throws<InvalidOperationException>(() => TelegramCredentials.Load(null, token, chatId));
    }
}
