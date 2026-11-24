using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoRuntimeTests
{
    [Theory]
    [InlineData(Direction.Long)]
    [InlineData(Direction.Short)]
    public async Task ActualFillTargetsPartialExitBreakEvenAndRestartReleaseCapacity(Direction direction)
    {
        using Fixture fixture = new(direction);
        await fixture.EnterAsync();
        Assert.False(await fixture.CycleAsync()); // Rebase the attached stop.
        Assert.False(await fixture.CycleAsync()); // Claim both targets.
        Assert.True(await fixture.CycleAsync());
        Assert.Equal(2, fixture.Exchange.Targets.Count);
        decimal expectedStop = direction == Direction.Long ? 96m : 103.9m;
        Assert.Equal(expectedStop, fixture.Exchange.Stop);
        FakeOrder first = fixture.Exchange.Targets.Single(order => order.Client.StartsWith("tp1-", StringComparison.Ordinal));
        fixture.Exchange.Last = first.Trigger!.Value;
        fixture.Exchange.Fill(first, first.Quantity / 2m, cancelled: true);
        Assert.False(await fixture.CycleAsync()); // Remainder of TP1 is durably submitted.
        Assert.Equal(expectedStop, fixture.Exchange.Stop);
        fixture.Restart();
        Assert.False(await fixture.CycleAsync()); // TP1 actually completed: request entry-price stop.
        Assert.Equal(fixture.Exchange.Average, fixture.Exchange.Stop);
        Assert.True(await fixture.CycleAsync());
        Assert.Equal(1.25m, Assert.Single(fixture.Store.ReadActive()).PositionQuantity);
        Assert.Equal(1.25m, fixture.Store.FilledExitQuantity(fixture.Signal.SignalId, "TP1"));
        FakeOrder second = fixture.Exchange.Targets.Single(order => order.Client.StartsWith("tp2-", StringComparison.Ordinal));
        fixture.Exchange.Last = second.Trigger!.Value;
        fixture.Exchange.Fill(second, second.Quantity, cancelled: false);
        fixture.Restart();
        Assert.True(await fixture.CycleAsync());
        Assert.Empty(fixture.Store.ReadActive());
        Assert.Equal(BybitDemoReservationState.Closed, Assert.Single(fixture.Store.ReadAll()).State);
        Assert.Equal(2.5m, fixture.Store.ReadExitFills(fixture.Signal.SignalId).Sum(fill => fill.Quantity));
        Assert.Equal(1, fixture.Exchange.EntryRequests);
    }

    [Fact]
    public async Task LostEntryAndTargetResponsesRecoverWithoutDuplicateSubmission()
    {
        using Fixture fixture = new(Direction.Long);
        fixture.Exchange.LoseNextEntryResponse = true;
        Assert.Equal(BybitDemoEntryRequestResult.OutcomeUnknown, await fixture.EnterAsync());
        fixture.Restart();
        Assert.False(await fixture.CycleAsync());
        fixture.Exchange.LoseNextTargetResponse = true;
        await Assert.ThrowsAsync<BybitDemoSubmissionUnknownException>(() => fixture.CycleAsync());
        fixture.Restart();
        Assert.False(await fixture.CycleAsync());
        Assert.True(await fixture.CycleAsync());
        Assert.Equal(1, fixture.Exchange.EntryRequests);
        Assert.Equal(2, fixture.Exchange.Targets.Count);
    }

    [Fact]
    public async Task UnknownTargetDoesNotSuppressSafetyExitWhenStopDisappears()
    {
        using Fixture fixture = new(Direction.Long);
        await fixture.EnterAsync();
        await fixture.CycleAsync();
        await fixture.CycleAsync();
        fixture.Exchange.HideTargetLookup = true;
        fixture.Exchange.StopMissing = true;
        Assert.False(await fixture.CycleAsync());
        Assert.Contains(fixture.Exchange.Orders, order => order.Client.StartsWith("safety-", StringComparison.Ordinal));
        Assert.Equal(0m, fixture.Exchange.Remaining);
    }

    [Fact]
    public async Task ManuallyAmendedTargetCannotBeReportedReady()
    {
        using Fixture fixture = new(Direction.Long);
        await fixture.EnterAsync();
        await fixture.CycleAsync();
        await fixture.CycleAsync();
        fixture.Exchange.Targets[0].Trigger += 1m;
        Assert.False(await fixture.CycleAsync());
        Assert.Equal(2.5m, fixture.Exchange.Remaining);
        Assert.Equal(96m, fixture.Exchange.Stop);
        Assert.Equal(2, fixture.Exchange.Targets.Count);
    }

    [Theory]
    [InlineData(Direction.Long, "30m")]
    [InlineData(Direction.Short, "30m")]
    [InlineData(Direction.Long, "1h")]
    [InlineData(Direction.Short, "1h")]
    public async Task DonchianChannelClosesActualPositionAfterRestartWithoutAnActiveSignal(Direction direction, string timeframe)
    {
        using Fixture fixture = new(direction, SetupType.DonchianFlowBreakout, timeframe);
        await fixture.EnterAsync();
        await fixture.CycleAsync();
        await fixture.CycleAsync();
        Assert.True(await fixture.CycleAsync());
        Assert.Single(fixture.Exchange.Targets);
        fixture.Clock.Now = fixture.Clock.Now.AddHours(2);
        fixture.Exchange.ChannelBreach = true;
        fixture.Restart();
        Assert.False(await fixture.CycleAsync());
        Assert.Contains(fixture.Exchange.Orders, order => order.Client.StartsWith("channel-", StringComparison.Ordinal));
        Assert.Equal(0m, fixture.Exchange.Remaining);
        Assert.True(await fixture.CycleAsync());
        Assert.Empty(fixture.Store.ReadActive());
    }

    [Theory]
    [InlineData(1.01, false)]
    [InlineData(0.01, true)]
    public async Task PartialEntryUsesActualQuantityAndUnsplittableDustClosesSafely(decimal quantity, bool safetyExit)
    {
        using Fixture fixture = new(Direction.Long);
        fixture.Exchange.EntryFillQuantity = quantity;
        await fixture.EnterAsync();
        await fixture.CycleAsync();
        if (safetyExit)
        {
            Assert.Equal(0m, fixture.Exchange.Remaining);
            Assert.Contains(fixture.Exchange.Orders, order => order.Client.StartsWith("safety-", StringComparison.Ordinal));
        }
        else
        {
            await fixture.CycleAsync();
            Assert.Equal(0.5m, fixture.Exchange.Targets.Single(order => order.Client.StartsWith("tp1-", StringComparison.Ordinal)).Quantity);
            Assert.Equal(0.51m, fixture.Exchange.Targets.Single(order => order.Client.StartsWith("tp2-", StringComparison.Ordinal)).Quantity);
            Assert.Equal(quantity, Assert.Single(fixture.Store.ReadActive()).PositionQuantity);
        }
    }

    [Fact]
    public async Task StopExecutionCancelsRemainingTargetsBeforeReleasingCapacity()
    {
        using Fixture fixture = new(Direction.Long);
        await fixture.EnterAsync();
        await fixture.CycleAsync();
        await fixture.CycleAsync();
        fixture.Exchange.ExecuteStop();
        Assert.False(await fixture.CycleAsync());
        fixture.Restart();
        Assert.True(await fixture.CycleAsync());
        Assert.Empty(fixture.Store.ReadActive());
        Assert.All(fixture.Exchange.Targets, order => Assert.Equal("Cancelled", order.Status));
        Assert.Equal(2.5m, fixture.Store.ReadExitFills(fixture.Signal.SignalId).Sum(fill => fill.Quantity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignOrdersOrCrossMarginPreventAllExchangeMutations(bool foreignOrder)
    {
        using Fixture fixture = new(Direction.Long);
        await fixture.EnterAsync();
        if (foreignOrder) fixture.Exchange.Orders.Add(new FakeOrder
        {
            Id = "manual-order",
            Client = "manual-order",
            Side = "Sell",
            Quantity = 0.1m,
            Reduce = true,
            Status = "New"
        });
        else fixture.Exchange.MarginMode = "REGULAR_MARGIN";
        int mutations = fixture.Exchange.Mutations;
        Assert.False(await fixture.CycleAsync());
        Assert.Equal(mutations, fixture.Exchange.Mutations);
        Assert.Equal(2.5m, fixture.Exchange.Remaining);
    }

    [Fact]
    public async Task MissingStopClosesEvenWhenPublicMarketAndChannelAreUnavailable()
    {
        using Fixture fixture = new(Direction.Long, SetupType.DonchianFlowBreakout, "1h");
        await fixture.EnterAsync();
        fixture.Exchange.StopMissing = true;
        fixture.Exchange.PublicUnavailable = true;
        Assert.False(await fixture.CycleAsync());
        Assert.Equal(0m, fixture.Exchange.Remaining);
        Assert.Contains(fixture.Exchange.Orders, order => order.Client.StartsWith("safety-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JournalToEntryRefetchesAccountAndQuoteAfterInteractiveApproval(bool crossMargin)
    {
        using Fixture fixture = new(Direction.Long);
        fixture.SeedJournals();
        fixture.OnApprove = () =>
        {
            if (crossMargin) fixture.Exchange.MarginMode = "REGULAR_MARGIN";
            else fixture.Exchange.Quote = 102m;
            return Task.FromResult(true);
        };
        BybitDemoRuntimeResult result = await fixture.StepAsync();
        Assert.Equal(crossMargin ? 0 : 1, result.Submissions);
        if (crossMargin) Assert.Empty(fixture.Exchange.Orders);
        else Assert.Equal(2.45m, fixture.Exchange.Orders.Single(order => !order.Reduce).Quantity);
    }

    [Fact]
    public async Task EarlyStopExecutionEventWaitsForRestIdentityInsteadOfPermanentlyBlockingRecovery()
    {
        using Fixture fixture = new(Direction.Long);
        await fixture.EnterAsync();
        BybitDemoSession session = new("fixture-key", "fixture-secret", fixture.Clock, 30);
        session.ObserveFrame(Encoding.UTF8.GetBytes("""
            {"topic":"execution.linear","data":[{"category":"linear","symbol":"BTCUSDT","orderLinkId":"","orderId":"stop-order"}]}
            """));
        Assert.False(session.ValidateEvents(fixture.Store));
        await fixture.CycleAsync();
        await fixture.CycleAsync();
        Assert.True(await fixture.CycleAsync());
        Assert.True(session.ValidateEvents(fixture.Store));
        BybitDemoReservation next = Assert.Single(fixture.Store.ReadActive()).Reservation with
        {
            SignalId = new string('2', 64),
            Symbol = "ETHUSDT",
            ClientOrderId = "demo-next"
        };
        Assert.Equal(BybitDemoReservationResult.Reserved, fixture.Store.TryReserve(next));
    }

    [Fact]
    public async Task PendingStopUpdateDoesNotSuppressDonchianChannelExit()
    {
        using Fixture fixture = new(Direction.Long, SetupType.DonchianFlowBreakout, "1h");
        await fixture.EnterAsync();
        await fixture.CycleAsync();
        await fixture.CycleAsync();
        Assert.True(await fixture.CycleAsync());
        Assert.Equal(BybitDemoStopClaimResult.Claimed, fixture.Store.TryClaimStopChange(
            new("pending-stop", fixture.Signal.SignalId, 2.5m, fixture.Exchange.Stop, 96.1m, fixture.Clock.Now)));
        fixture.Clock.Now = fixture.Clock.Now.AddHours(2);
        fixture.Exchange.ChannelBreach = true;
        fixture.Restart();
        Assert.False(await fixture.CycleAsync());
        Assert.Equal(0m, fixture.Exchange.Remaining);
        Assert.Contains(fixture.Exchange.Orders, order => order.Client.StartsWith("channel-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedTargetTriggerSourceOrDirectionBlocksReadiness(bool changeSource)
    {
        using Fixture fixture = new(Direction.Long);
        await fixture.EnterAsync();
        await fixture.CycleAsync();
        await fixture.CycleAsync();
        fixture.Exchange.WrongTargetSource = changeSource;
        fixture.Exchange.WrongTargetDirection = !changeSource;
        Assert.False(await fixture.CycleAsync());
        Assert.Equal(2.5m, fixture.Exchange.Remaining);
        Assert.Equal(2, fixture.Exchange.Targets.Count);
    }

    [Fact]
    public async Task WithheldOrRevokedApprovalBlocksEntriesButNotOwnedSafetyRecovery()
    {
        using Fixture fixture = new(Direction.Long);
        fixture.SeedJournals();
        fixture.OnApprove = () => Task.FromResult(false);
        Assert.Equal("awaiting-demo-authorization", (await fixture.StepAsync()).State);
        Assert.Empty(fixture.Exchange.Orders);
        fixture.OnApprove = () => Task.FromResult(true);
        Assert.Equal(1, (await fixture.StepAsync()).Submissions);
        fixture.OnApprove = () => Task.FromResult(false);
        fixture.Exchange.StopMissing = true;
        await fixture.StepAsync();
        Assert.Equal(0m, fixture.Exchange.Remaining);
        Assert.Contains(fixture.Exchange.Orders, order => order.Client.StartsWith("safety-", StringComparison.Ordinal));
        Assert.Equal(1, fixture.Exchange.EntryRequests);
    }

    [Fact]
    public async Task ApprovalRevokedDuringRefreshedPreflightPreventsDispatch()
    {
        using Fixture fixture = new(Direction.Long);
        fixture.SeedJournals();
        int approvals = 0;
        fixture.OnApprove = () => Task.FromResult(++approvals == 1);
        Assert.Equal("awaiting-demo-authorization", (await fixture.StepAsync()).State);
        Assert.Empty(fixture.Exchange.Orders);
        Assert.Empty(fixture.Store.ReadActive());
    }

    [Fact]
    public async Task FreshStreamCanSubmitWhileOtherSymbolsAndTimeframesAreUnavailable()
    {
        using Fixture fixture = new(Direction.Long);
        fixture.SeedJournals(includeOtherStreams: false);
        fixture.Symbols.Add("ETHUSDT");
        BybitDemoRuntimeResult result = await fixture.StepAsync();
        Assert.Equal(1, result.Submissions);
        Assert.Equal("monitoring-partial-public-streams", result.State);
        Assert.Equal(1, fixture.Exchange.EntryRequests);
        Assert.Equal("BTCUSDT", Assert.Single(fixture.Store.ReadActive()).Reservation.Symbol);
    }

    [Fact]
    public async Task OwnSourceCanceledAfterDiscoveryBlocksBeforeEntryHttpReads()
    {
        using Fixture fixture = new(Direction.Long);
        fixture.SeedJournals(includeOtherStreams: false);
        int checks = 0;
        fixture.OnHealth = () =>
        {
            if (++checks == 2) fixture.CancelSource();
            return new(true, fixture.Clock.Now, 1);
        };
        Assert.Equal("entry-preflight-blocked", (await fixture.StepAsync()).State);
        Assert.Equal(0, fixture.Exchange.ReadRequests);
        Assert.Equal(0, fixture.Exchange.EntryRequests);
        Assert.Empty(fixture.Store.ReadActive());
    }

    [Fact]
    public async Task OwnSourceCanceledDuringApprovalCannotBeSubmitted()
    {
        using Fixture fixture = new(Direction.Long);
        fixture.SeedJournals(includeOtherStreams: false);
        fixture.OnApprove = () =>
        {
            fixture.CancelSource();
            return Task.FromResult(true);
        };
        Assert.Equal("entry-preflight-blocked", (await fixture.StepAsync()).State);
        Assert.Equal(0, fixture.Exchange.EntryRequests);
        Assert.Empty(fixture.Store.ReadActive());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bybit-runtime-{Guid.NewGuid():N}");
        private readonly string _path;
        public readonly FakeClock Clock = new();
        public readonly FakeExchange Exchange;
        public BybitDemoSignalCandidate Signal;
        public SqliteBybitDemoReservationStore Store { get; private set; }
        private readonly BybitDemoRestClient _rest;
        private readonly BybitDemoPublicMarketClient _market;
        private readonly HttpClient _binance;
        private readonly BybitDemoConfiguration _config;
        private BybitDemoRuntime _runtime;
        public Func<Task<bool>> OnApprove = () => Task.FromResult(true);
        public readonly HashSet<string> Symbols = new(StringComparer.Ordinal) { "BTCUSDT" };
        public Func<BybitDemoPrivateHealth>? OnHealth;
        private SignalScenarioProjectionEngine? _sourceEngine;
        public Fixture(Direction direction, SetupType family = SetupType.FlowAtrRecovery, string timeframe = "15m")
        {
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "demo.sqlite");
            Store = SqliteBybitDemoReservationStore.Open(_path, 20);
            Exchange = new(Clock, direction);
            Signal = new(new string('1', 64), new string('a', 64), "BTCUSDT", family, timeframe, direction,
                Clock.Now, 100m, direction == Direction.Long ? 95m : 105m,
                direction == Direction.Long ? 105m : 95m,
                family == SetupType.DonchianFlowBreakout ? null : direction == Direction.Long ? 110m : 90m);
            _config = BybitDemoConfiguration.Parse(JsonSerializer.Serialize(new
            {
                formatVersion = 1,
                mode = "bybit-demo",
                targetNotionalUsdt = 250,
                maximumPositions = 20,
                maximumPriceDifferencePercent = 10,
                maximumSlippagePercent = 10,
                maximumLeverage = 10,
                marginMode = "isolated",
                maximumSignalAgeSeconds = 60,
                maximumQuoteAgeSeconds = 5,
                privateStreamFreshnessSeconds = 30,
                receiveWindowMilliseconds = 5000,
                statePath = _path,
                signalJournalPaths = new[] { Path.Combine(_directory, "flow.sqlite"), Path.Combine(_directory, "30.sqlite"), Path.Combine(_directory, "1h.sqlite") }
            }));
            _rest = new(Exchange, "fixture-key", "fixture-secret", Clock, 5000);
            _market = new(new ForwardHandler(Exchange), Clock);
            _binance = new(new ForwardHandler(Exchange));
            _runtime = CreateRuntime();
        }
        public async Task<BybitDemoEntryRequestResult> EnterAsync()
        {
            BybitDemoInstrument instrument = await _market.GetInstrumentAsync("BTCUSDT", TestContext.Current.CancellationToken);
            BybitDemoEntryInputs input = new(Signal, new("BTCUSDT", 100m, 100m, Clock.Now, Clock.Now), instrument,
                new("ISOLATED_MARGIN", Clock.Now), new("BTCUSDT", 0m, null, 5m, null, null, false, Clock.Now),
                BybitDemoAccountComparisonResult.NoMismatchObserved, true, true, Clock.Now, Clock.Now);
            return await new BybitDemoEntryExecutor(Store, _rest, Clock, _config).RequestAsync(input, TestContext.Current.CancellationToken);
        }
        public Task<bool> CycleAsync() => _runtime.ReconcileTradeAsync(Assert.Single(Store.ReadAll()), TestContext.Current.CancellationToken);
        public Task<BybitDemoRuntimeResult> StepAsync() => _runtime.StepAsync(TestContext.Current.CancellationToken);
        public void SeedJournals(bool includeOtherStreams = true)
        {
            UtcTimestamp close = new(Clock.Now);
            UtcTimestamp open = new(Clock.Now.AddMinutes(-15));
            UtcTimestamp context = new(Clock.Now.AddMinutes(-75));
            SignalScenarioPolicySet policies = new(1,
                new SignalScenarioExpiryPolicy(1, "expiry-v1", TimeSpan.FromHours(1)),
                new SignalScenarioRearmPolicy(1, "rearm-v1", TimeSpan.FromMinutes(2), true),
                new SignalScenarioInvalidationPolicy(1, "invalidation-v1", true, true));
            SignalScenarioProjectionEngine engine = new(policies);
            _sourceEngine = engine;
            SignalScenarioAnchor anchor = SignalScenarioAnchor.Create(new("BTCUSDT"), "BINANCE_USDM", "15m",
                Direction.Long, SetupType.FlowAtrRecovery, "profile-v1", "flow-v1", "anchor", open, 1);
            string stream = SignalOnlyStreamId.For("BTCUSDT", "15m");
            SignalScenarioInput input = SignalScenarioInput.Confirm(stream, "confirm", anchor, close, close, 2);
            SignalScenarioProjectionTransition transition = engine.Apply(engine.CreateInitialSnapshot(), input);
            SignalProvenance provenance = SignalProvenance.Create("profile-v1", new string('a', 64),
                new(close, 2), new(context, 0),
                [new("binance-public", "native-kline-v1", "anchor", Digest("anchor"), SignalInputRole.Setup, "15m", open, open, 1),
                 new("binance-public", "native-kline-v1", "context", Digest("context"), SignalInputRole.HigherTimeframe, "1h", context, context, 0)]);
            SignalEvidenceRecord evidence = SignalEvidenceRecord.Create(anchor.Identity, provenance, SignalEvidenceState.Complete, ["fixture-reviewed"]);
            SignalPriceLevels levels = new(1, "fixture-levels", SignalPriceLevelPolicy.CurrentFormulaVersion,
                14, new(SetupType.FlowAtrRecovery, 1.5m, 0.1m, 1m, 2m), "15m", Direction.Long, "anchor",
                100m, 99m, 2m, SignalPriceLevelsState.Ready, 97m, 103m, 106m, null);
            SignalRecord signal = SignalRecord.Create(anchor.Identity, evidence, SignalAssessmentState.ConfirmedOnClosedBar,
                new(open, close), close, new("binance-public", "native-kline-v1", "anchor", open, new(100m)), priceLevels: levels);
            using (SqliteSignalJournalStore journal = SqliteSignalJournalStore.Open(_config.SignalJournalPaths[0]))
            {
                journal.Append(new(stream, 0, 1, input.EventId, Digest(input.EventId), close, close, 2,
                    input.InputHash, transition.NextSnapshot, transition.EmittedEvents, signal, evidence));
                if (includeOtherStreams)
                    journal.Append(new(SignalOnlyStreamId.For("BTCUSDT", "30m"), 0, 1, "idle", Digest("idle"),
                        close, close, 2, null, engine.CreateInitialSnapshot(), []));
            }
            for (int index = 1; index < 3; index++)
            {
                using SqliteSignalJournalStore journal = SqliteSignalJournalStore.Open(_config.SignalJournalPaths[index]);
                if (includeOtherStreams)
                    journal.Append(new(SignalOnlyStreamId.For("BTCUSDT", index == 1 ? "30m" : "1h"), 0, 1,
                        "idle", Digest("idle"), close, close, 2, null, engine.CreateInitialSnapshot(), []));
            }
            Signal = new(signal.Identity.SignalId, signal.RecordHash, "BTCUSDT", SetupType.FlowAtrRecovery,
                "15m", Direction.Long, Clock.Now, 100m, 97m, 103m, 106m);
        }
        public void CancelSource()
        {
            Clock.Now = Clock.Now.AddSeconds(1);
            UtcTimestamp at = new(Clock.Now);
            string stream = SignalOnlyStreamId.For("BTCUSDT", "15m");
            using SqliteSignalJournalStore journal = SqliteSignalJournalStore.Open(_config.SignalJournalPaths[0]);
            SignalJournalSnapshot before = journal.Read(stream);
            SignalScenarioInput input = SignalScenarioInput.Invalidate(stream, "cancel", Signal.SignalId, "exit", at, at, 3);
            SignalScenarioProjectionTransition transition = (_sourceEngine ?? throw new InvalidOperationException()).Apply(before.ScenarioSnapshot!, input);
            Assert.True(journal.Append(new(stream, 1, 2, input.EventId, Digest(input.EventId), at, at, 3,
                input.InputHash, transition.NextSnapshot, transition.EmittedEvents)));
        }
        private static string Digest(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        public void Restart() { Store = SqliteBybitDemoReservationStore.Open(_path, 20); _runtime = CreateRuntime(); }
        private BybitDemoRuntime CreateRuntime() => new(Store, _rest, _market, new(_binance, Clock), _config,
            Symbols, Clock, () => OnHealth?.Invoke() ?? new(true, Clock.Now, 1), () => true, (_, _) => OnApprove());
        public void Dispose() { _rest.Dispose(); _market.Dispose(); _binance.Dispose(); Directory.Delete(_directory, true); }
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FakeOrder
    {
        public required string Id;
        public required string Client;
        public required string Side;
        public decimal Quantity;
        public decimal Filled;
        public decimal? Trigger;
        public string Status = "Untriggered";
        public bool Reduce;
        public bool Stop;
        public readonly List<object> Fills = [];
    }
    private sealed class ForwardHandler(FakeExchange exchange) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => exchange.HandleAsync(request, cancellationToken);
    }
    private sealed class FakeExchange(FakeClock clock, Direction direction) : HttpMessageHandler
    {
        public readonly List<FakeOrder> Orders = [];
        public List<FakeOrder> Targets => Orders.Where(order => order.Client.StartsWith("tp", StringComparison.Ordinal)).ToList();
        public decimal Remaining;
        public decimal Average => direction == Direction.Long ? 101m : 99m;
        public decimal Stop;
        public decimal Last = 100m;
        public int EntryRequests;
        public int ReadRequests;
        public bool LoseNextEntryResponse;
        public bool LoseNextTargetResponse;
        public bool HideTargetLookup;
        public bool StopMissing;
        private string EntryClient => Orders.Single(order => !order.Reduce).Client;
        public bool ChannelBreach;
        public decimal? EntryFillQuantity;
        public string MarginMode = "ISOLATED_MARGIN";
        public decimal Quote = 100m;
        public bool PublicUnavailable;
        public int Mutations;
        public bool WrongTargetSource;
        public bool WrongTargetDirection;
        private FakeOrder? _filledStop;
        public void ExecuteStop()
        {
            _filledStop = new FakeOrder
            {
                Id = "stop-order",
                Client = "",
                Side = direction == Direction.Long ? "Sell" : "Buy",
                Quantity = Remaining,
                Trigger = Stop,
                Reduce = true,
                Stop = true
            };
            Last = Stop;
            Fill(_filledStop, Remaining, false);
        }
        public void Fill(FakeOrder order, decimal quantity, bool cancelled)
        {
            clock.Now = clock.Now.AddMilliseconds(1);
            order.Filled += quantity;
            order.Status = cancelled ? "Cancelled" : "Filled";
            Remaining += order.Reduce ? -quantity : quantity;
            order.Fills.Add(new
            {
                symbol = "BTCUSDT",
                orderId = order.Id,
                orderLinkId = order.Client,
                side = order.Side,
                execId = $"{order.Id}-fill-{order.Fills.Count}",
                execType = "Trade",
                execPrice = N(order.Reduce ? Last : Average),
                execQty = N(quantity),
                execFee = "0.01",
                closedSize = N(order.Reduce ? quantity : 0m),
                execTime = clock.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                seq = 1
            });
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => HandleAsync(request, cancellationToken);
        public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) ReadRequests++;
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;
            Dictionary<string, string> query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]), StringComparer.Ordinal);
            if (host == "fapi.binance.com")
            {
                Assert.Equal("/fapi/v1/klines", path);
                long duration = query["interval"] == "30m" ? 1_800_000L : 3_600_000L;
                long start = long.Parse(query["startTime"], CultureInfo.InvariantCulture);
                long end = long.Parse(query["endTime"], CultureInfo.InvariantCulture);
                List<object[]> candles = [];
                for (long open = start; open <= end && candles.Count < 1000; open += duration)
                {
                    bool breach = ChannelBreach && open >= new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    candles.Add([open, "100", breach && direction == Direction.Short ? "103" : "102",
                        breach && direction == Direction.Long ? "97" : "98", "100", "0", open + duration - 1]);
                }
                return Raw(JsonSerializer.Serialize(candles));
            }
            Assert.Equal("api-demo.bybit.com", host);
            if (request.Method == HttpMethod.Post) Mutations++;
            if (PublicUnavailable && (host == "fapi.binance.com" || path.StartsWith("/v5/market/", StringComparison.Ordinal)))
                throw new HttpRequestException("fixture public outage");
            if (path == "/v5/market/orderbook") return Response(new
            {
                s = "BTCUSDT",
                b = new[] { new[] { N(Quote), "10" } },
                a = new[] { new[] { N(Quote), "10" } },
                ts = clock.Now.ToUnixTimeMilliseconds()
            });
            if (path == "/v5/order/create")
            {
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                JsonElement root = body.RootElement;
                bool reduce = root.TryGetProperty("reduceOnly", out JsonElement flag) && flag.GetBoolean();
                FakeOrder order = new()
                {
                    Id = "order-" + Orders.Count,
                    Client = root.GetProperty("orderLinkId").GetString()!,
                    Side = root.GetProperty("side").GetString()!,
                    Quantity = D(root, "qty"),
                    Reduce = reduce,
                    Trigger = root.TryGetProperty("triggerPrice", out _) ? D(root, "triggerPrice") : null
                };
                Orders.Add(order);
                if (!reduce) { EntryRequests++; Stop = D(root, "stopLoss"); Fill(order, EntryFillQuantity ?? order.Quantity, EntryFillQuantity is not null); }
                else if (order.Trigger is null) Fill(order, Math.Min(order.Quantity, Remaining), false);
                if (!reduce && LoseNextEntryResponse) { LoseNextEntryResponse = false; throw new HttpRequestException("fixture lost acknowledgement"); }
                if (reduce && LoseNextTargetResponse) { LoseNextTargetResponse = false; throw new HttpRequestException("fixture lost target acknowledgement"); }
                return Response(new { orderId = order.Id, orderLinkId = order.Client });
            }
            if (path == "/v5/position/trading-stop")
            {
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Stop = D(body.RootElement, "stopLoss");
                return Response(new { });
            }
            if (path == "/v5/order/cancel")
            {
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                string id = body.RootElement.GetProperty("orderId").GetString()!;
                FakeOrder? order = Orders.SingleOrDefault(order => order.Id == id);
                if (order is not null) order.Status = "Cancelled";
                return Response(new { orderId = id });
            }
            if (path == "/v5/account/info") return Response(new { marginMode = MarginMode });
            if (path == "/v5/position/list" && !query.ContainsKey("symbol") && Remaining == 0m)
                return Response(new { category = "linear", nextPageCursor = "", list = Array.Empty<object>() });
            if (path == "/v5/market/instruments-info") return Raw("""
                {"retCode":0,"result":{"category":"linear","nextPageCursor":"","list":[{
                "symbol":"BTCUSDT","status":"Trading","contractType":"LinearPerpetual","quoteCoin":"USDT","settleCoin":"USDT",
                "unifiedMarginTrade":true,"isPreListing":false,"priceFilter":{"minPrice":"0.1","maxPrice":"1999999.8","tickSize":"0.1"},
                "lotSizeFilter":{"qtyStep":"0.01","minOrderQty":"0.01","minNotionalValue":"5","maxMktOrderQty":"150"},
                "leverageFilter":{"maxLeverage":"100"}}]}}
                """);
            if (path == "/v5/market/tickers") return Response(new { list = new[] { new { symbol = "BTCUSDT", lastPrice = N(Last) } } });
            if (path == "/v5/position/list") return Response(new
            {
                category = "linear",
                nextPageCursor = "",
                list = new[]
                {
                    new
                    {
                        symbol = "BTCUSDT",
                        size = N(Remaining),
                        avgPrice = N(Remaining == 0m ? 0m : Average),
                        stopLoss = N(StopMissing || Remaining == 0m ? 0m : Stop),
                        side = Remaining == 0m ? "" : direction == Direction.Long ? "Buy" : "Sell",
                        leverage = "5",
                        positionIdx = 0,
                        positionStatus = "Normal",
                        isReduceOnly = false,
                        seq = 1
                    }
                }
            });
            if (path == "/v5/execution/list") return Response(new
            {
                category = "linear",
                nextPageCursor = "",
                list = query["orderId"] == "stop-order" ? _filledStop?.Fills.ToArray() ?? [] : Orders.Single(order => order.Id == query["orderId"]).Fills.ToArray()
            });
            if (path is "/v5/order/realtime" or "/v5/order/history")
            {
                List<object> facts = [];
                bool lookup = query.ContainsKey("orderId") || query.ContainsKey("orderLinkId");
                foreach (FakeOrder order in Orders)
                {
                    if (lookup && (query.TryGetValue("orderId", out string? id) ? order.Id != id : order.Client != query["orderLinkId"])) continue;
                    if (lookup && HideTargetLookup && order.Client.StartsWith("tp", StringComparison.Ordinal)) continue;
                    if (!lookup && path.EndsWith("realtime", StringComparison.Ordinal) && SqliteBybitDemoReservationStore.Terminal(order.Status)) continue;
                    facts.Add(Fact(order));
                }
                if (Orders.Count > 0 && (!lookup || query.GetValueOrDefault("orderId") == "stop-order"))
                {
                    bool terminal = Remaining == 0m || StopMissing;
                    if (path.EndsWith("history", StringComparison.Ordinal) || lookup || !terminal)
                        facts.Add(Fact(_filledStop ?? new FakeOrder
                        {
                            Id = "stop-order",
                            Client = "",
                            Side = direction == Direction.Long ? "Sell" : "Buy",
                            Quantity = Remaining == 0m ? EntryFillQuantity ?? 2.5m : Remaining,
                            Trigger = Stop,
                            Status = terminal ? "Cancelled" : "Untriggered",
                            Reduce = true,
                            Stop = true
                        }));
                }
                return Response(new { category = "linear", nextPageCursor = "", list = facts });
            }
            throw new InvalidOperationException("Unexpected fixture endpoint: " + path);
        }
        private object Fact(FakeOrder order) => new
        {
            symbol = "BTCUSDT",
            orderId = order.Id,
            orderLinkId = order.Client,
            parentOrderLinkId = order.Stop ? EntryClient : "",
            side = order.Side,
            qty = N(order.Quantity),
            cumExecQty = N(order.Filled),
            avgPrice = order.Filled > 0m ? N(Average) : "",
            orderStatus = order.Status,
            reduceOnly = order.Reduce,
            closeOnTrigger = order.Stop,
            positionIdx = 0,
            triggerPrice = N(order.Trigger ?? 0m),
            triggerBy = order.Trigger is null ? "UNKNOWN" : !order.Stop && WrongTargetSource ? "MarkPrice" : "LastPrice",
            stopOrderType = order.Stop ? "StopLoss" : "Stop",
            triggerDirection = order.Trigger is null ? 0 : order.Stop ? direction == Direction.Long ? 2 : 1
                : WrongTargetDirection ? 0 : direction == Direction.Long ? 1 : 2,
            orderType = "Market"
        };
        private static decimal D(JsonElement root, string name) => decimal.Parse(root.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
        private static string N(decimal value) => value.ToString(CultureInfo.InvariantCulture);
        private static HttpResponseMessage Response(object result) => Raw(JsonSerializer.Serialize(new { retCode = 0, result }));
        private static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
