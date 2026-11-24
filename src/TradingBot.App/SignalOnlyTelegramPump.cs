using System.Diagnostics;
using System.Text.Json;
using TradingBot.Core;
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
        string[] auditStreams = streams.SelectMany(static group => group).Distinct(StringComparer.Ordinal).ToArray();
        int auditIndex = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (IReadOnlyList<string> timeframes in streams)
            {
                long pollStarted = Stopwatch.GetTimestamp();
                IReadOnlyList<SignalOutboxDeliverySnapshot> results =
                    await sender.DispatchTimeframesAsync(timeframes,
                        timeframes.Count > 1 ? TimeSpan.FromSeconds(5) : TimeSpan.Zero,
                        cancellationToken).ConfigureAwait(false);
                double pollMilliseconds = Stopwatch.GetElapsedTime(pollStarted).TotalMilliseconds;
                foreach (SignalOutboxDeliverySnapshot result in results)
                {
                    SignalDeliveryReceipt? attempted = result.Receipts.LastOrDefault(
                        static receipt => receipt.Status == SignalDeliveryStatus.Attempted);
                    SignalDeliveryReceipt? acknowledged = attempted is null ? null
                        : result.Receipts.LastOrDefault(receipt =>
                            receipt.Status == SignalDeliveryStatus.Acknowledged
                            && receipt.DeliveryAttemptId == attempted.DeliveryAttemptId);
                    await output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        channel = "Telegram",
                        status = result.Status.ToString(),
                        outboxId = result.OutboxId,
                        attemptsStarted = result.AttemptsStarted,
                        createdAt = result.CreatedAt,
                        attemptedAt = attempted?.OccurredAt.Value,
                        acknowledgedAt = acknowledged?.OccurredAt.Value,
                        queueMilliseconds = attempted is null ? (double?)null
                            : (attempted.OccurredAt.Value - result.CreatedAt).TotalMilliseconds,
                        apiMilliseconds = acknowledged is null || attempted is null ? (double?)null
                            : (acknowledged.OccurredAt.Value - attempted.OccurredAt.Value).TotalMilliseconds,
                        pollMilliseconds
                    })).ConfigureAwait(false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (auditStreams.Length != 0)
            {
                sender.ValidateJournalStream(auditStreams[auditIndex]);
                auditIndex = (auditIndex + 1) % auditStreams.Length;
            }
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
