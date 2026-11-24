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

    internal void ValidateJournalStream(string streamId) => _ = _signalJournal.Read(streamId);

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
            if (!_deliveryStore.HasPotentialWork(_signalJournal, streamId))
            {
                continue;
            }
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
                if (!_deliveryStore.HasPotentialWork(_signalJournal, streamId))
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
            "Исследовательский сигнал (не ордер)",
            $"Инструмент: {signal.Identity.InstrumentId.Value}",
            $"Направление: {DirectionText(signal.Identity.Direction)}",
            $"Стратегия: {FamilyText(signal.Identity.Family)}",
            $"Таймфрейм: {TimeframeText(signal.Identity.SetupTimeframe)}",
            $"Оценка: {AssessmentText(signal.AssessmentState)}",
            $"Время решения (UTC): {signal.DecisionAt.Value.ToUniversalTime():O}",
            $"Время события сценария (UTC): {item.ScenarioEvent.AvailabilityTime.Value.ToUniversalTime():O}",
            $"Событие сценария: {ScenarioEventText(item.ScenarioEvent.Kind)}",
            "Статистическая проверка: неизвестно"
        ];
        lines.Add($"Цена закрытия для ориентира (не цена исполнения): {signal.ReferencePrice.Price.Value.ToString(CultureInfo.InvariantCulture)}");
        if (signal.Identity.Family == SetupType.DonchianFlowBreakout)
        {
            string? exitReason = item.ScenarioEvent.ReasonCode switch
            {
                "opposite-channel-55-breakout" => "обратный пробой канала 55",
                "channel-stop-touched" => "касание модельного стопа",
                "channel-target-touched" => "касание модельной цели",
                "channel-exit-ambiguous" => "стоп и цель или обратный пробой в одной свече; порядок неизвестен",
                _ => null
            };
            if (exitReason is not null)
            {
                lines.Add($"Причина завершения сценария: {exitReason}; исполнение не моделируется.");
            }
            string Value(string key)
            {
                string prefix = key + ":";
                string? reason = evidence.QualityReasons.FirstOrDefault(item =>
                    item.StartsWith(prefix, StringComparison.Ordinal));
                return reason is null ? "неизвестно" : reason[prefix.Length..];
            }
            lines.Add($"Канал 55: нижняя {Value("channel-lower-55")}; верхняя {Value("channel-upper-55")}");
            lines.Add($"Тень сигнальной свечи: минимум {Value("signal-low")}; максимум {Value("signal-high")}");
            lines.Add($"Поток WMA 9 / 34: {Value("flow-fast-wma-9")} / {Value("flow-slow-wma-34")}");
            lines.Add($"Объём / средний объём 20: {Value("volume")} / {Value("volume-mean-20")}");
            lines.Add($"Границы 13: {Value("channel-lower-13")} / {Value("channel-upper-13")}; 21: {Value("channel-lower-21")} / {Value("channel-upper-21")}");
            lines.Add($"Границы 89: {Value("channel-lower-89")} / {Value("channel-upper-89")}; 144: {Value("channel-lower-144")} / {Value("channel-upper-144")}");
            if (evidence.QualityReasons.Any(static item => item.StartsWith("model-stop-period:", StringComparison.Ordinal)))
            {
                lines.Add($"Модельный стоп по каналу {Value("model-stop-period")}: {Value("model-stop")}");
            }
            if (evidence.QualityReasons.Any(static item => item.StartsWith("model-target-period:", StringComparison.Ordinal)))
            {
                lines.Add($"Модельная цель по каналу {Value("model-target-period")}: {Value("model-target")}");
            }
            lines.Add("Канальные уровни — исследовательские ориентиры, не защитные ордера или гарантированные цели.");
        }
        if (signal.PriceLevels is { State: SignalPriceLevelsState.Ready } levels)
        {
            lines.Add($"Ориентировочный стоп-лосс: {levels.StopLoss!.Value.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"Цель 1: {levels.TakeProfit1!.Value.ToString(CultureInfo.InvariantCulture)}; цель 2: {levels.TakeProfit2!.Value.ToString(CultureInfo.InvariantCulture)}");
            lines.Add("Уровни исследовательские: это не ордера и не цены исполнения; округление по шагу биржи не выполнено.");
        }
        else if (signal.Identity.Family != SetupType.DonchianFlowBreakout)
        {
            lines.Add($"Стоп-лосс и цели: недоступны ({ReasonText(signal.PriceLevels?.UnknownReason)})");
        }
        if (evidence.MarketContext is { } context)
        {
            lines.Add($"VWAP сессии UTC: {context.Vwap.Value?.ToString(CultureInfo.InvariantCulture) ?? ReasonText(context.Vwap.UnknownReason)}");
            lines.Add($"POC наблюдаемых сделок: {context.Poc.Price?.ToString(CultureInfo.InvariantCulture) ?? ReasonText(context.Poc.UnknownReason)} (шаг {context.Poc.BinSize.ToString(CultureInfo.InvariantCulture)}, сделок: {context.Poc.TradeCount})");
            lines.Add(context.LatestLiquidation is { } liquidation
                ? $"Выборочная ликвидация: {TradeSideText(liquidation.Side)} {liquidation.Quantity.ToString(CultureInfo.InvariantCulture)} по {liquidation.Price.ToString(CultureInfo.InvariantCulture)} (поток неполный)"
                : "Выборочные ликвидации: за период не наблюдались (поток неполный)");
        }
        lines.Add($"ID сигнала: {signal.Identity.SignalId}");
        lines.Add($"ID доставки: {item.OutboxId}");
        string message = string.Join('\n', lines);
        if (message.Length > 4096)
        {
            throw new InvalidDataException(
                "The formatted Telegram signal exceeds the Bot API text limit.");
        }
        return message;
    }

    private static string DirectionText(Direction value) => value switch
    {
        Direction.Long => "лонг",
        Direction.Short => "шорт",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string FamilyText(SetupType value) => value switch
    {
        SetupType.BoundaryRejection => "Отбой от границы",
        SetupType.CenterRetest => "Ретест центра канала",
        SetupType.FailedBreakoutReclaim => "Возврат после ложного пробоя",
        SetupType.BreakoutRetestContinuation => "Продолжение после пробоя и ретеста",
        SetupType.FlowStochasticRecovery => "Восстановление потока по стохастику",
        SetupType.FlowRsiRecovery => "Восстановление потока по RSI",
        SetupType.FlowAtrRecovery => "Восстановление потока по ATR",
        SetupType.VolatilityCompressionExpansion => "Расширение после сжатия волатильности",
        SetupType.DonchianFlowBreakout => "Пробой 55 с потоком и объёмом",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string TimeframeText(string value) => value switch
    {
        "5m" => "5 минут",
        "15m" => "15 минут",
        "30m" => "30 минут",
        "1h" => "1 час",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string AssessmentText(SignalAssessmentState value) => value switch
    {
        SignalAssessmentState.Research => "исследовательская",
        SignalAssessmentState.ConfirmedOnClosedBar => "подтверждено на закрытой свече",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string ScenarioEventText(SignalScenarioEventKind value) => value switch
    {
        SignalScenarioEventKind.AlertCreated => "сигнал создан",
        SignalScenarioEventKind.Continued => "сценарий продолжается",
        SignalScenarioEventKind.Canceled => "сценарий отменён",
        SignalScenarioEventKind.Expired => "срок сценария истёк",
        SignalScenarioEventKind.OverlapLinked => "связано пересечение сценариев",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string TradeSideText(TradeSide value) => value switch
    {
        TradeSide.Buy => "покупка",
        TradeSide.Sell => "продажа",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string ReasonText(string? value) => value switch
    {
        null => "не записано в старом сигнале",
        "price-levels-signal-bar-unavailable" => "нет свечи сигнала",
        "price-levels-atr-unavailable" => "ATR недоступен",
        "session-history-incomplete" => "история сессии неполная",
        "session-volume-unavailable" => "объём сессии недоступен",
        "session-volume-overflow" => "переполнение объёма сессии",
        "trade-window-incomplete" => "окно сделок неполное",
        "trade-source-time-unavailable" => "нет времени источника сделки",
        "trade-input-invalid" => "некорректные данные сделки",
        "no-observed-trades" => "нет наблюдаемых сделок",
        _ => $"недоступно; код: {value}"
    };
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
