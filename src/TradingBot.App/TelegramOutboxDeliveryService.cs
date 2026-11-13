using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>One-message, output-only dispatcher for committed signal outbox rows.</summary>
public sealed class TelegramOutboxDeliveryService
{
    private readonly SqliteSignalJournalStore _signalJournal;
    private readonly SqliteSignalOutboxDeliveryStore _deliveryStore;
    private readonly HttpClient _httpClient;
    private readonly SignalOutboxDeliveryPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly string _botToken;
    private readonly string _chatId;

    public TelegramOutboxDeliveryService(SqliteSignalJournalStore signalJournal,
        SqliteSignalOutboxDeliveryStore deliveryStore, HttpClient httpClient,
        SignalOutboxDeliveryPolicy policy, string botToken, string chatId,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(signalJournal);
        ArgumentNullException.ThrowIfNull(deliveryStore);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(policy);
        if (!IsSafeBotToken(botToken))
        {
            throw new ArgumentException("A valid Telegram bot token is required.",
                nameof(botToken));
        }
        if (!IsSafeChatId(chatId))
        {
            throw new ArgumentException("A valid Telegram chat ID is required.",
                nameof(chatId));
        }

        _signalJournal = signalJournal;
        _deliveryStore = deliveryStore;
        _httpClient = httpClient;
        _policy = policy;
        _botToken = botToken;
        _chatId = chatId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SignalOutboxDeliverySnapshot?> DispatchNextAsync(
        string streamId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset claimedAt = _timeProvider.GetUtcNow();
        SignalOutboxLease? lease = _deliveryStore.ClaimNext(_signalJournal,
            streamId, _policy, claimedAt);
        if (lease is null)
        {
            return null;
        }

        if (!_deliveryStore.BeginAttempt(lease, _policy, _timeProvider.GetUtcNow()))
        {
            return FindDelivery(lease.StreamId, lease.Item.OutboxId);
        }

        TelegramSendResult result = await TelegramBotApiClient.SendMessageAsync(
            _httpClient, _botToken, _chatId,
            TelegramSignalMessageFormatter.Format(lease.Item, lease.Signal, lease.Evidence),
            cancellationToken).ConfigureAwait(false);
        RecordResult(lease, result, _timeProvider.GetUtcNow());
        return FindDelivery(lease.StreamId, lease.Item.OutboxId);
    }

    internal async Task<IReadOnlyList<SignalOutboxDeliverySnapshot>> DispatchTimeframesAsync(
        IReadOnlyList<string> streamIds, TimeSpan groupingDelay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamIds);
        if (groupingDelay < TimeSpan.Zero || groupingDelay >= _policy.LeaseDuration
            || streamIds.Count > 4
            || streamIds.Distinct(StringComparer.Ordinal).Count() != streamIds.Count)
        {
            throw new ArgumentException("The timeframe streams or grouping delay are invalid.", nameof(streamIds));
        }

        cancellationToken.ThrowIfCancellationRequested();
        SignalOutboxLease? first = null;
        foreach (string streamId in streamIds)
        {
            first = _deliveryStore.ClaimNext(_signalJournal, streamId, _policy,
                _timeProvider.GetUtcNow());
            if (first is not null)
            {
                break;
            }
        }
        if (first is null)
        {
            return [];
        }

        List<SignalOutboxLease> leases = [first];
        if (streamIds.Count > 1
            && first.Item.ScenarioEvent.Kind == SignalScenarioEventKind.AlertCreated)
        {
            TimeSpan remaining = first.Signal.BarInterval.CloseTime.Value
                + groupingDelay - _timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining > groupingDelay ? groupingDelay : remaining,
                    cancellationToken).ConfigureAwait(false);
            }
            foreach (string streamId in streamIds)
            {
                if (StringComparer.Ordinal.Equals(streamId, first.StreamId))
                {
                    continue;
                }
                SignalOutboxLease? lease = _deliveryStore.ClaimNext(_signalJournal,
                    streamId, _policy, _timeProvider.GetUtcNow());
                if (lease is not null)
                {
                    leases.Add(lease);
                }
            }
        }

        List<SignalOutboxDeliverySnapshot> snapshots = [];
        var groups = leases.GroupBy(static lease => (
            Instrument: lease.Signal.Identity.InstrumentId.Value,
            lease.Signal.Identity.Direction,
            Close: lease.Signal.BarInterval.CloseTime.Value,
            Unique: lease.Item.ScenarioEvent.Kind == SignalScenarioEventKind.AlertCreated
                ? string.Empty : lease.Item.OutboxId));
        foreach (IGrouping<(string Instrument, Direction Direction, DateTimeOffset Close, string Unique),
                     SignalOutboxLease> group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<SignalOutboxLease> started = [];
            foreach (SignalOutboxLease lease in group)
            {
                if (_deliveryStore.BeginAttempt(lease, _policy, _timeProvider.GetUtcNow()))
                {
                    started.Add(lease);
                }
            }
            TelegramSendResult? result = null;
            if (started.Count != 0)
            {
                string message = TelegramSignalMessageFormatter.FormatGroup(started);
                result = await TelegramBotApiClient.SendMessageAsync(_httpClient, _botToken,
                    _chatId, message, cancellationToken).ConfigureAwait(false);
                DateTimeOffset completedAt = _timeProvider.GetUtcNow();
                foreach (SignalOutboxLease lease in started)
                {
                    RecordResult(lease, result, completedAt);
                }
            }
            foreach (SignalOutboxLease lease in group)
            {
                if (FindDelivery(lease.StreamId, lease.Item.OutboxId) is { } snapshot)
                {
                    snapshots.Add(snapshot);
                }
            }
            if (result?.Kind == TelegramSendResultKind.RateLimited)
            {
                break;
            }
        }
        return snapshots;
    }

    private void RecordResult(SignalOutboxLease lease, TelegramSendResult result,
        DateTimeOffset completedAt)
    {
        switch (result.Kind)
        {
            case TelegramSendResultKind.Acknowledged:
                _deliveryStore.RecordAcknowledgement(lease, _policy, completedAt,
                    result.ExternalMessageId!);
                break;
            case TelegramSendResultKind.RateLimited:
                _deliveryStore.RecordFailure(lease, _policy, completedAt,
                    SignalOutboxFailureKind.RateLimited, result.RetryAfterSeconds);
                break;
            case TelegramSendResultKind.PermanentFailure:
                _deliveryStore.RecordFailure(lease, _policy, completedAt,
                    SignalOutboxFailureKind.Permanent);
                break;
            case TelegramSendResultKind.UnknownDelivery:
                _deliveryStore.RecordUnknownDelivery(lease, _policy, completedAt);
                break;
            default:
                throw new InvalidOperationException(
                    "The Telegram sender returned an unsupported result.");
        }
    }

    private SignalOutboxDeliverySnapshot? FindDelivery(string streamId,
        string outboxId) => _deliveryStore.Read(streamId).FirstOrDefault(item =>
            StringComparer.Ordinal.Equals(item.OutboxId, outboxId));

    private static bool IsSafeBotToken(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && value.All(static character => char.IsAsciiLetterOrDigit(character)
            || character is ':' or '_' or '-');

    private static bool IsSafeChatId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(static character => char.IsAsciiLetterOrDigit(character)
            || character is '@' or '_' or '-');
}

internal static class TelegramSignalMessageFormatter
{
    public static string FormatGroup(IReadOnlyList<SignalOutboxLease> leases)
    {
        ArgumentNullException.ThrowIfNull(leases);
        if (leases.Count == 0)
        {
            throw new ArgumentException("A Telegram message requires a committed signal.", nameof(leases));
        }
        string message = string.Join("\n\n", leases.Select(static lease =>
            Format(lease.Item, lease.Signal, lease.Evidence)));
        if (message.Length > 4096)
        {
            throw new InvalidDataException(
                "The grouped Telegram signal exceeds the Bot API text limit.");
        }
        return message;
    }

    public static string Format(SignalJournalOutboxItem item, SignalRecord signal,
        SignalEvidenceRecord evidence)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!StringComparer.Ordinal.Equals(item.ScenarioEvent.SignalId,
                signal.Identity.SignalId)
            || !StringComparer.Ordinal.Equals(item.SignalRecordHash, signal.RecordHash)
            || !StringComparer.Ordinal.Equals(signal.EvidenceHash, evidence.RecordHash))
        {
            throw new InvalidDataException(
                "The Telegram message does not match its committed signal record.");
        }

        List<string> lines =
        [
            "RESEARCH signal (not an order)",
            $"Instrument: {signal.Identity.InstrumentId.Value}",
            $"Direction: {signal.Identity.Direction}",
            $"Setup: {signal.Identity.Family}",
            $"Timeframe: {signal.Identity.SetupTimeframe}",
            $"Assessment: {signal.AssessmentState}",
            $"Decision time (UTC): {signal.DecisionAt.Value.ToUniversalTime():O}",
            $"Scenario time (UTC): {item.ScenarioEvent.AvailabilityTime.Value.ToUniversalTime():O}",
            $"Scenario event: {item.ScenarioEvent.Kind}",
            "Statistical validation: UNKNOWN"
        ];
        if (evidence.MarketContext is { } context)
        {
            lines.Add($"UTC-session VWAP: {context.Vwap.Value?.ToString(CultureInfo.InvariantCulture) ?? context.Vwap.UnknownReason}");
            lines.Add($"Observed-trade POC bin: {context.Poc.Price?.ToString(CultureInfo.InvariantCulture) ?? context.Poc.UnknownReason} (bin {context.Poc.BinSize.ToString(CultureInfo.InvariantCulture)}, {context.Poc.TradeCount} trades)");
            lines.Add(context.LatestLiquidation is { } liquidation
                ? $"Sampled liquidation: {liquidation.Side} {liquidation.Quantity.ToString(CultureInfo.InvariantCulture)} @ {liquidation.Price.ToString(CultureInfo.InvariantCulture)} (incomplete feed)"
                : "Sampled liquidations: none observed in the window (incomplete feed)");
        }
        lines.Add($"Signal ID: {signal.Identity.SignalId}");
        lines.Add($"Delivery ID: {item.OutboxId}");
        string message = string.Join('\n', lines);
        if (message.Length > 4096)
        {
            throw new InvalidDataException(
                "The formatted Telegram signal exceeds the Bot API text limit.");
        }
        return message;
    }
}

internal enum TelegramSendResultKind
{
    Acknowledged,
    RateLimited,
    PermanentFailure,
    UnknownDelivery
}

internal sealed record TelegramSendResult(TelegramSendResultKind Kind,
    string? ExternalMessageId = null, int? RetryAfterSeconds = null);

internal static class TelegramBotApiClient
{
    private const string ApiEndpoint = "https://api.telegram.org/bot";
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<TelegramSendResult> SendMessageAsync(
        HttpClient httpClient, string botToken, string chatId, string text,
        CancellationToken cancellationToken)
    {
        string endpoint = string.Concat(ApiEndpoint, botToken, "/sendMessage");
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(
            new TelegramSendMessage(chatId, text), RequestJsonOptions),
            Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or OperationCanceledException or IOException)
        {
            return new TelegramSendResult(TelegramSendResultKind.UnknownDelivery);
        }

        using (response)
        {
            int statusCode = (int)response.StatusCode;
            if (statusCode == (int)HttpStatusCode.TooManyRequests)
            {
                int? retryAfterSeconds = null;
                try
                {
                    string rateLimitBody = await response.Content
                        .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using JsonDocument rateLimitDocument =
                        JsonDocument.Parse(rateLimitBody);
                    retryAfterSeconds = ReadRetryAfterSeconds(
                        rateLimitDocument.RootElement);
                }
                catch (Exception exception) when (exception is HttpRequestException
                    or OperationCanceledException or IOException or JsonException)
                {
                }
                return new TelegramSendResult(TelegramSendResultKind.RateLimited,
                    RetryAfterSeconds: retryAfterSeconds);
            }
            if (statusCode is >= 400 and < 500
                && statusCode != (int)HttpStatusCode.RequestTimeout)
            {
                return new TelegramSendResult(
                    TelegramSendResultKind.PermanentFailure);
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException
                or OperationCanceledException or IOException)
            {
                return new TelegramSendResult(TelegramSendResultKind.UnknownDelivery);
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return new TelegramSendResult(
                        TelegramSendResultKind.UnknownDelivery);
                }
                bool apiOk = root.TryGetProperty("ok", out JsonElement ok)
                    && ok.ValueKind == JsonValueKind.True;
                int? errorCode = ReadErrorCode(root);
                if (!apiOk && errorCode == (int)HttpStatusCode.TooManyRequests)
                {
                    return new TelegramSendResult(TelegramSendResultKind.RateLimited,
                        RetryAfterSeconds: ReadRetryAfterSeconds(root));
                }

                if (apiOk && response.IsSuccessStatusCode
                    && root.TryGetProperty("result", out JsonElement result)
                    && result.ValueKind == JsonValueKind.Object
                    && result.TryGetProperty("message_id", out JsonElement messageId)
                    && messageId.TryGetInt64(out long parsedMessageId)
                    && parsedMessageId > 0)
                {
                    return new TelegramSendResult(TelegramSendResultKind.Acknowledged,
                        parsedMessageId.ToString(CultureInfo.InvariantCulture));
                }

                if (!apiOk && IsClientError(errorCode))
                {
                    return new TelegramSendResult(
                        TelegramSendResultKind.PermanentFailure);
                }
            }
            catch (JsonException)
            {
                return new TelegramSendResult(TelegramSendResultKind.UnknownDelivery);
            }
        }

        return new TelegramSendResult(TelegramSendResultKind.UnknownDelivery);

    }

    private static int? ReadErrorCode(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("error_code", out JsonElement errorCode)
        && errorCode.TryGetInt32(out int value) ? value : null;

    private static int? ReadRetryAfterSeconds(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("parameters", out JsonElement parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("retry_after", out JsonElement retryAfter)
            || !retryAfter.TryGetInt64(out long seconds)
            || seconds <= 0)
        {
            return null;
        }
        return seconds >= int.MaxValue ? int.MaxValue : (int)seconds;
    }

    private static bool IsClientError(int? statusCode) =>
        statusCode is >= 400 and < 500;

    private sealed record TelegramSendMessage(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text);
}
