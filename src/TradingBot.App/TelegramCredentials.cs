using System.Text;

namespace TradingBot.App;

internal sealed class TelegramCredentials
{
    private TelegramCredentials(string token, string chatId) { Token = token; ChatId = chatId; }
    public string Token { get; }
    public string ChatId { get; }

    public static TelegramCredentials Load(string? directory, string? environmentToken, string? environmentChatId)
    {
        try
        {
            string? token = environmentToken;
            string? chatId = environmentChatId;
            if (directory is not null)
            {
                if (!Path.IsPathFullyQualified(directory)) throw new InvalidDataException();
                token = Read(Path.Combine(directory, "telegram-bot-token"), 256);
                chatId = Read(Path.Combine(directory, "telegram-chat-id"), 128);
            }
            if (!Valid(token, 256, token: true) || !Valid(chatId, 128, token: false)) throw new InvalidDataException();
            return new(token!, chatId!);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException("Telegram requires a complete valid credential pair from the selected source; no fallback is used after a file error.");
        }
    }

    private static string Read(string path, int maximum)
    {
        Span<byte> buffer = stackalloc byte[257];
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int count = stream.ReadAtLeast(buffer[..(maximum + 1)], maximum + 1, throwOnEndOfStream: false);
        if (count == 0 || count > maximum) throw new InvalidDataException();
        foreach (byte value in buffer[..count]) if (value is < 33 or > 126) throw new InvalidDataException();
        return Encoding.ASCII.GetString(buffer[..count]);
    }

    private static bool Valid(string? value, int maximum, bool token)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximum) return false;
        foreach (char character in value)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-') && character != (token ? ':' : '@')) return false;
        return true;
    }
}
