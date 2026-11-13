using System.Text.Json;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>Polls committed output rows without altering signal or delivery semantics.</summary>
internal static class SignalOnlyTelegramPump
{
    internal static async Task RunAsync(TelegramOutboxDeliveryService sender,
        IEnumerable<IReadOnlyList<string>> instrumentStreams, TextWriter output, TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(instrumentStreams);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        IReadOnlyList<string>[] streams = instrumentStreams.ToArray();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (IReadOnlyList<string> timeframes in streams)
            {
                IReadOnlyList<SignalOutboxDeliverySnapshot> results =
                    await sender.DispatchTimeframesAsync(timeframes,
                        timeframes.Count > 1 ? TimeSpan.FromSeconds(5) : TimeSpan.Zero,
                        cancellationToken).ConfigureAwait(false);
                foreach (SignalOutboxDeliverySnapshot result in results)
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        channel = "Telegram",
                        status = result.Status.ToString(),
                        outboxId = result.OutboxId,
                        attemptsStarted = result.AttemptsStarted
                    })).ConfigureAwait(false);
                }
            }
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
