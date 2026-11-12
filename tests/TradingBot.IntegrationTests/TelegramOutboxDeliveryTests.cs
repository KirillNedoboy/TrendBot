using System.Net;
using System.Text;
using System.Text.Json;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class TelegramOutboxDeliveryTests
{
    private const string StreamId = "binance-usdm-native-kline:BTCUSDT";
    private const string TestBotToken = "12345:SECRET_SENTINEL";
    private const string TestChatId = "-1001234567890";
    private static readonly DateTimeOffset DeliveryStart = At(15);

    [Fact]
    public void CommittedOutboxClaimRecoversLeaseBeforeSendWithoutCountingAttempt()
    {
        using TemporaryDatabase journalDatabase = new("journal");
        using TemporaryDatabase deliveryDatabase = new("delivery");
        SignalOutboxDeliveryPolicy policy = CreatePolicy();
        using SqliteSignalJournalStore journal = CreateJournal(journalDatabase.Path,
            new ScenarioFixture());
        SignalOutboxLease firstLease;
        using (SqliteSignalOutboxDeliveryStore delivery =
            SqliteSignalOutboxDeliveryStore.Open(deliveryDatabase.Path))
        {
            firstLease = Assert.IsType<SignalOutboxLease>(delivery.ClaimNext(journal,
                StreamId, policy, DeliveryStart));
            Assert.Equal(Assert.Single(journal.Read(StreamId).Outbox).OutboxId,
                firstLease.Item.OutboxId);
            SignalOutboxDeliverySnapshot leased = Assert.Single(delivery.Read(StreamId));
            Assert.Equal(SignalOutboxDeliveryStatus.Leased, leased.Status);
            Assert.Equal(0, leased.AttemptsStarted);
            Assert.Empty(leased.Receipts);
        }

        DateTimeOffset recoveredAt = DeliveryStart.Add(policy.LeaseDuration);
        using SqliteSignalOutboxDeliveryStore reopened =
            SqliteSignalOutboxDeliveryStore.Open(deliveryDatabase.Path);
        SignalOutboxLease recovered = Assert.IsType<SignalOutboxLease>(
            reopened.ClaimNext(journal, StreamId, policy, recoveredAt));
        Assert.Equal(firstLease.Item.OutboxId, recovered.Item.OutboxId);
        Assert.Equal(1, recovered.NextAttemptNumber);
        Assert.True(reopened.BeginAttempt(recovered, policy, recoveredAt));
        SignalOutboxDeliverySnapshot sending = Assert.Single(reopened.Read(StreamId));
        Assert.Equal(SignalOutboxDeliveryStatus.Sending, sending.Status);
        Assert.Equal(1, sending.AttemptsStarted);
        Assert.Equal(SignalDeliveryStatus.Attempted,
            Assert.Single(sending.Receipts).Status);
    }

    [Fact]
    public void ExpiredSendingLeaseRecoversAsUnknownAndCannotBeClaimedAgain()
    {
        using TemporaryDatabase journalDatabase = new("journal");
        using TemporaryDatabase deliveryDatabase = new("delivery");
        SignalOutboxDeliveryPolicy policy = CreatePolicy();
        using SqliteSignalJournalStore journal = CreateJournal(journalDatabase.Path,
            new ScenarioFixture());
        using (SqliteSignalOutboxDeliveryStore delivery =
            SqliteSignalOutboxDeliveryStore.Open(deliveryDatabase.Path))
        {
            SignalOutboxLease lease = Assert.IsType<SignalOutboxLease>(
                delivery.ClaimNext(journal, StreamId, policy, DeliveryStart));
            Assert.True(delivery.BeginAttempt(lease, policy, DeliveryStart));
        }

        DateTimeOffset recoveredAt = DeliveryStart.Add(policy.LeaseDuration);
        using SqliteSignalOutboxDeliveryStore reopened =
            SqliteSignalOutboxDeliveryStore.Open(deliveryDatabase.Path);
        Assert.Null(reopened.ClaimNext(journal, StreamId, policy, recoveredAt));
        SignalOutboxDeliverySnapshot recovered = Assert.Single(reopened.Read(StreamId));
        Assert.Equal(SignalOutboxDeliveryStatus.UnknownDelivery, recovered.Status);
        Assert.Equal(1, recovered.AttemptsStarted);
        Assert.Equal(new[] { SignalDeliveryStatus.Attempted, SignalDeliveryStatus.Unknown },
            recovered.Receipts.Select(static receipt => receipt.Status));
        Assert.Null(recovered.ExternalReceiptId);
    }

    [Fact]
    public async Task TimeoutAfterApiAcceptanceIsUnknownAndStableIdIsVisibleWithoutSecrets()
    {
        using TemporaryDatabase journalDatabase = new("journal");
        using TemporaryDatabase deliveryDatabase = new("delivery");
        ScenarioFixture fixture = new();
        using SqliteSignalJournalStore journal = CreateJournal(journalDatabase.Path,
            fixture);
        bool apiAccepted = false;
        string? postedText = null;
        using HttpClient http = new(new DelegateHandler(async (request, token) =>
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!
                .ReadAsStringAsync(token));
            postedText = body.RootElement.GetProperty("text").GetString();
            apiAccepted = true;
            throw new HttpRequestException(TestBotToken);
        }));
        SignalOutboxDeliveryPolicy policy = CreatePolicy();
        using SqliteSignalOutboxDeliveryStore delivery =
            SqliteSignalOutboxDeliveryStore.Open(deliveryDatabase.Path);
        TelegramOutboxDeliveryService sender = new(journal, delivery, http, policy,
            TestBotToken, TestChatId, new MutableTimeProvider(DeliveryStart));

        SignalOutboxDeliverySnapshot result = Assert.IsType<SignalOutboxDeliverySnapshot>(
            await sender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken));

        Assert.True(apiAccepted);
        Assert.Equal(SignalOutboxDeliveryStatus.UnknownDelivery, result.Status);
        Assert.Contains(fixture.FirstOutboxId, postedText, StringComparison.Ordinal);
        Assert.Equal(1, result.AttemptsStarted);
        Assert.DoesNotContain(TestBotToken, CoreJsonSerializer.Serialize(result),
            StringComparison.Ordinal);
        Assert.DoesNotContain("api.telegram.org", CoreJsonSerializer.Serialize(result),
            StringComparison.Ordinal);
        Assert.Null(await sender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MalformedApiResponseShapeIsDurablyUnknownAndRedacted()
    {
        using TemporaryDatabase journalDatabase = new("journal");
        using TemporaryDatabase deliveryDatabase = new("delivery");
        using SqliteSignalJournalStore journal = CreateJournal(journalDatabase.Path,
            new ScenarioFixture());
        using HttpClient http = new(new DelegateHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, "\"MALFORMED_SENTINEL\""))));
        SignalOutboxDeliveryPolicy policy = CreatePolicy();
        using SqliteSignalOutboxDeliveryStore delivery =
            SqliteSignalOutboxDeliveryStore.Open(deliveryDatabase.Path);
        TelegramOutboxDeliveryService sender = new(journal, delivery, http, policy,
            TestBotToken, TestChatId, new MutableTimeProvider(DeliveryStart));

        SignalOutboxDeliverySnapshot result = Assert.IsType<SignalOutboxDeliverySnapshot>(
            await sender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken));

        Assert.Equal(SignalOutboxDeliveryStatus.UnknownDelivery, result.Status);
        Assert.Equal(new[] { SignalDeliveryStatus.Attempted, SignalDeliveryStatus.Unknown },
            result.Receipts.Select(static receipt => receipt.Status));
        Assert.DoesNotContain("MALFORMED_SENTINEL",
            CoreJsonSerializer.Serialize(delivery.Read(StreamId)),
            StringComparison.Ordinal);
        Assert.Null(await sender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApiAcknowledgmentAndMessageIdSurviveDeliveryJournalReopen()
    {
        using TemporaryDatabase journalDatabase = new("journal");
        using TemporaryDatabase deliveryDatabase = new("delivery");
        using SqliteSignalJournalStore journal = CreateJournal(journalDatabase.Path,
            new ScenarioFixture());
        using HttpClient http = new(new DelegateHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK,
                "{\"ok\":true,\"result\":{\"message_id\":41}}"))));
        SignalOutboxDeliveryPolicy policy = CreatePolicy();
        string outboxId;
        using (SqliteSignalOutboxDeliveryStore delivery =
            SqliteSignalOutboxDeliveryStore.Open(deliveryDatabase.Path))
        {
            TelegramOutboxDeliveryService sender = new(journal, delivery, http,
                policy, TestBotToken, TestChatId,
                new MutableTimeProvider(DeliveryStart));
            SignalOutboxDeliverySnapshot result = Assert.IsType<SignalOutboxDeliverySnapshot>(
                await sender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken));
            outboxId = result.OutboxId;
            Assert.Equal(SignalOutboxDeliveryStatus.Acknowledged, result.Status);
            Assert.Equal("41", result.ExternalReceiptId);
            SignalDeliveryReceipt receipt = Assert.Single(result.Receipts,
                static item => item.Status == SignalDeliveryStatus.Acknowledged);
            Assert.Equal("41", receipt.ExternalReceiptId);
            Assert.Equal(SignalDeliveryChannel.Telegram, receipt.Channel);
        }

        using SqliteSignalOutboxDeliveryStore reopened =
            SqliteSignalOutboxDeliveryStore.Open(deliveryDatabase.Path);
        SignalOutboxDeliverySnapshot restored = Assert.Single(reopened.Read(StreamId));
        Assert.Equal(outboxId, restored.OutboxId);
        Assert.Equal(SignalOutboxDeliveryStatus.Acknowledged, restored.Status);
        Assert.Equal("41", restored.ExternalReceiptId);
        Assert.Equal(2, restored.Receipts.Count);
    }

    [Fact]
    public async Task RetryAfterBlocksAllRowsUntilItsExactTimeThenRetriesSameStableId()
    {
        using TemporaryDatabase journalDatabase = new("journal");
        using TemporaryDatabase deliveryDatabase = new("delivery");
        ScenarioFixture fixture = new();
        using SqliteSignalJournalStore journal = CreateJournal(journalDatabase.Path,
            fixture, includeContinuation: true);
        int requestCount = 0;
        string? firstText = null;
        using HttpClient http = new(new DelegateHandler(async (request, token) =>
        {
            requestCount++;
            using JsonDocument body = JsonDocument.Parse(await request.Content!
                .ReadAsStringAsync(token));
            firstText ??= body.RootElement.GetProperty("text").GetString();
            return requestCount == 1
                ? JsonResponse(HttpStatusCode.TooManyRequests,
                    "{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":120},\"description\":\"redact-me\"}")
                : JsonResponse(HttpStatusCode.OK,
                    "{\"ok\":true,\"result\":{\"message_id\":42}}");
        }));
        SignalOutboxDeliveryPolicy policy = CreatePolicy(maxAge: TimeSpan.FromHours(1));
        using SqliteSignalOutboxDeliveryStore delivery =
            SqliteSignalOutboxDeliveryStore.Open(deliveryDatabase.Path);
        MutableTimeProvider clock = new(DeliveryStart);
        TelegramOutboxDeliveryService sender = new(journal, delivery, http,
            policy, TestBotToken, TestChatId, clock);

        SignalOutboxDeliverySnapshot limited = Assert.IsType<SignalOutboxDeliverySnapshot>(
            await sender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken));
        Assert.Equal(SignalOutboxDeliveryStatus.Pending, limited.Status);
        Assert.Equal(DeliveryStart.AddSeconds(120), limited.NextAttemptAt);
        Assert.Equal(1, limited.AttemptsStarted);
        clock.Now = DeliveryStart.AddSeconds(119);
        Assert.Null(await sender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken));
        Assert.Equal(1, requestCount);
        clock.Now = DeliveryStart.AddSeconds(120);
        SignalOutboxDeliverySnapshot acknowledged =
            Assert.IsType<SignalOutboxDeliverySnapshot>(
                await sender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken));
        Assert.Equal(limited.OutboxId, acknowledged.OutboxId);
        Assert.Equal(SignalOutboxDeliveryStatus.Acknowledged, acknowledged.Status);
        Assert.Equal(2, acknowledged.AttemptsStarted);
        Assert.Contains(limited.OutboxId, firstText, StringComparison.Ordinal);
        Assert.DoesNotContain("redact-me", CoreJsonSerializer.Serialize(
            delivery.Read(StreamId)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PermanentFailureAndRetryExhaustionStopWithoutLoops()
    {
        using TemporaryDatabase permanentJournalPath = new("permanent-journal");
        using TemporaryDatabase permanentDeliveryPath = new("permanent-delivery");
        using SqliteSignalJournalStore permanentJournal = CreateJournal(
            permanentJournalPath.Path, new ScenarioFixture());
        int permanentCalls = 0;
        using HttpClient permanentHttp = new(new DelegateHandler((_, _) =>
        {
            permanentCalls++;
            return Task.FromResult(JsonResponse(HttpStatusCode.Forbidden,
                "{\"ok\":false,\"error_code\":403,\"description\":\"SECRET_SENTINEL\"}"));
        }));
        SignalOutboxDeliveryPolicy policy = CreatePolicy();
        using SqliteSignalOutboxDeliveryStore permanentDelivery =
            SqliteSignalOutboxDeliveryStore.Open(permanentDeliveryPath.Path);
        TelegramOutboxDeliveryService permanentSender = new(permanentJournal,
            permanentDelivery, permanentHttp, policy, TestBotToken, TestChatId,
            new MutableTimeProvider(DeliveryStart));
        SignalOutboxDeliverySnapshot permanent =
            Assert.IsType<SignalOutboxDeliverySnapshot>(
                await permanentSender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken));
        Assert.Equal(SignalOutboxDeliveryStatus.PermanentFailure, permanent.Status);
        Assert.Null(await permanentSender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken));
        Assert.Equal(1, permanentCalls);
        Assert.DoesNotContain(TestBotToken,
            CoreJsonSerializer.Serialize(permanentDelivery.Read(StreamId)),
            StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_SENTINEL",
            CoreJsonSerializer.Serialize(permanentDelivery.Read(StreamId)),
            StringComparison.Ordinal);

        using TemporaryDatabase retryJournalPath = new("retry-journal");
        using TemporaryDatabase retryDeliveryPath = new("retry-delivery");
        using SqliteSignalJournalStore retryJournal = CreateJournal(retryJournalPath.Path,
            new ScenarioFixture());
        int retryCalls = 0;
        using HttpClient retryHttp = new(new DelegateHandler((_, _) =>
        {
            retryCalls++;
            return Task.FromResult(JsonResponse(HttpStatusCode.TooManyRequests,
                "{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":1}}"));
        }));
        SignalOutboxDeliveryPolicy twoAttemptPolicy = CreatePolicy(maxAttempts: 2,
            maxAge: TimeSpan.FromHours(1));
        using SqliteSignalOutboxDeliveryStore retryDelivery =
            SqliteSignalOutboxDeliveryStore.Open(retryDeliveryPath.Path);
        MutableTimeProvider retryClock = new(DeliveryStart);
        TelegramOutboxDeliveryService retrySender = new(retryJournal, retryDelivery,
            retryHttp, twoAttemptPolicy, TestBotToken, TestChatId, retryClock);
        _ = await retrySender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken);
        retryClock.Now = DeliveryStart.AddSeconds(1);
        SignalOutboxDeliverySnapshot exhausted =
            Assert.IsType<SignalOutboxDeliverySnapshot>(
                await retrySender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken));
        Assert.Equal(SignalOutboxDeliveryStatus.RetryExhausted, exhausted.Status);
        Assert.Equal(2, exhausted.AttemptsStarted);
        Assert.Null(await retrySender.DispatchNextAsync(StreamId, TestContext.Current.CancellationToken));
        Assert.Equal(2, retryCalls);
    }

    [Fact]
    public void ExpiredOutboxRowIsDurablyExpiredWithoutAnAttempt()
    {
        using TemporaryDatabase journalDatabase = new("journal");
        using TemporaryDatabase deliveryDatabase = new("delivery");
        using SqliteSignalJournalStore journal = CreateJournal(journalDatabase.Path,
            new ScenarioFixture());
        SignalOutboxDeliveryPolicy policy = CreatePolicy(maxAge: TimeSpan.FromMinutes(10));
        using SqliteSignalOutboxDeliveryStore delivery =
            SqliteSignalOutboxDeliveryStore.Open(deliveryDatabase.Path);

        Assert.Null(delivery.ClaimNext(journal, StreamId, policy,
            DeliveryStart.Add(policy.MaxAge)));

        SignalOutboxDeliverySnapshot expired = Assert.Single(delivery.Read(StreamId));
        Assert.Equal(SignalOutboxDeliveryStatus.Expired, expired.Status);
        Assert.Equal(0, expired.AttemptsStarted);
        Assert.Empty(expired.Receipts);
    }

    private static SignalOutboxDeliveryPolicy CreatePolicy(int maxAttempts = 5,
        TimeSpan? maxAge = null) => new(maxAttempts,
        maxAge ?? TimeSpan.FromHours(24), TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(5));

    private static SqliteSignalJournalStore CreateJournal(string path,
        ScenarioFixture fixture, bool includeContinuation = false)
    {
        SqliteSignalJournalStore journal = SqliteSignalJournalStore.Open(path);
        Assert.True(journal.Append(fixture.FirstCommit));
        if (includeContinuation)
        {
            Assert.True(journal.Append(fixture.ContinuationCommit()));
        }
        return journal;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status,
        string json) => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static DateTimeOffset At(int minute) =>
        new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero)
            .AddMinutes(minute);
    private static UtcTimestamp UtcAt(int minute) => new(At(minute));

    private sealed class ScenarioFixture
    {
        private const string InputHash = "1111111111111111111111111111111111111111111111111111111111111111";
        private readonly SignalScenarioAnchor _anchor;
        private readonly SignalScenarioInput _firstInput;
        private readonly SignalScenarioProjectionTransition _firstTransition;
        private readonly SignalScenarioProjectionEngine _engine;
        private readonly SignalRecord _signal;
        private readonly string _firstOutboxId;

        public ScenarioFixture()
        {
            SignalScenarioPolicySet policies = new(
                SignalScenarioPolicySet.CurrentFormatVersion,
                new SignalScenarioExpiryPolicy(
                    SignalScenarioExpiryPolicy.CurrentFormatVersion, "expiry-v1",
                    TimeSpan.FromHours(1)),
                new SignalScenarioRearmPolicy(
                    SignalScenarioRearmPolicy.CurrentFormatVersion, "rearm-v1",
                    TimeSpan.FromMinutes(2), true),
                new SignalScenarioInvalidationPolicy(
                    SignalScenarioInvalidationPolicy.CurrentFormatVersion,
                    "invalidation-v1", true, true));
            _engine = new SignalScenarioProjectionEngine(policies);
            _anchor = SignalScenarioAnchor.Create(new InstrumentId("BTCUSDT"),
                "BINANCE_USDM", "15m", Direction.Long,
                SetupType.BoundaryRejection, "profile-v1",
                "boundary-rejection-v1", "anchor-event", UtcAt(0), 1);
            _firstInput = SignalScenarioInput.Confirm(StreamId,
                "first-confirmation", _anchor, UtcAt(15), UtcAt(15), 2);
            _firstTransition = _engine.Apply(_engine.CreateInitialSnapshot(),
                _firstInput);
            SignalProvenance provenance = SignalProvenance.Create("profile-v1",
                new string('a', 64), new CausalEventCursor(UtcAt(15), 2),
                new CausalEventCursor(UtcAt(-60), 0),
                [new SignalInputReference("binance-public", "native-kline-v1",
                     "anchor-event", new string('c', 64), SignalInputRole.Setup,
                     "15m", UtcAt(0), UtcAt(0), 1),
                 new SignalInputReference("binance-public", "native-kline-v1",
                     "context-event", new string('d', 64),
                     SignalInputRole.HigherTimeframe, "1h", UtcAt(-60), UtcAt(-60), 0)]);
            SignalEvidenceRecord evidence = SignalEvidenceRecord.Create(
                _anchor.Identity, provenance, SignalEvidenceState.Complete,
                ["fixture-reviewed"]);
            _signal = SignalRecord.Create(_anchor.Identity, evidence,
                SignalAssessmentState.ConfirmedOnClosedBar,
                new SignalBarInterval(UtcAt(0), UtcAt(15)), UtcAt(15),
                new SignalReferencePrice("binance-public", "native-kline-v1",
                    "anchor-event", UtcAt(0), new Price(100m)));
            FirstCommit = new SignalJournalCommit(StreamId, 0, 1,
                _firstInput.EventId, InputHash, _firstInput.EventTime,
                _firstInput.AvailabilityTime, _firstInput.StableSequence,
                _firstInput.InputHash, _firstTransition.NextSnapshot,
                _firstTransition.EmittedEvents, _signal, evidence);
            _firstOutboxId = _firstTransition.EmittedEvents[0].EventId;
        }

        public SignalJournalCommit FirstCommit { get; }
        public string FirstOutboxId => _firstOutboxId;

        public SignalJournalCommit ContinuationCommit()
        {
            SignalScenarioInput input = SignalScenarioInput.Confirm(StreamId,
                "second-confirmation", _anchor, UtcAt(16), UtcAt(16), 3);
            SignalScenarioProjectionTransition transition = _engine.Apply(
                _firstTransition.NextSnapshot, input);
            return new SignalJournalCommit(StreamId, 1, 2, input.EventId,
                new string('2', 64), input.EventTime, input.AvailabilityTime,
                input.StableSequence, input.InputHash, transition.NextSnapshot,
                transition.EmittedEvents);
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TemporaryDatabase(string name) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"tradingbot-telegram-{name}-{Guid.NewGuid():N}.sqlite");

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
