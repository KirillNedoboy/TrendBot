using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class HistoricalReplayRunnerTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void RunnerProcessesOnlyCausalPrefixesAndUsesAvailabilityCloseAndStableSequence()
    {
        ReplayEnvelope[] envelopes =
        [
            Envelope(new Trade("trade-late", Instrument, Utc(0), new Price(101m),
                new Quantity(1m), TradeSide.Buy), 3, Time(0), Time(3)),
            Envelope(new Bar("bar-close", Instrument, Utc(2), Utc(1), Utc(2),
                new Price(100m), new Price(105m), new Price(99m), new Price(104m),
                new Quantity(4m)), 2, Time(0), Time(0)),
            Envelope(new Trade("trade-first", Instrument, Utc(1), new Price(100m),
                new Quantity(1m), TradeSide.Buy), 1, Time(1), Time(1)),
            Envelope(new Trade("trade-same-time", Instrument, Utc(1), new Price(100.5m),
                new Quantity(1m), TradeSide.Buy), 4, Time(1), Time(1))
        ];

        List<string> observed = [];
        Dictionary<string, string[]> prefixes = [];
        ReplayRunResult result = new ReplayRunner().Run(
            "stream-a", envelopes,
            context =>
            {
                observed.Add(context.Event.EventId);
                prefixes.Add(context.Event.EventId, context.Prefix.Events
                    .Select(static item => item.CausalEvent.Payload.EventId).ToArray());
                Assert.DoesNotContain(context.Prefix.Events,
                    item => item.CausalEvent.Payload.EventId == "trade-late"
                        && context.Event.EventId != "trade-late");
            });

        Assert.True(result.Succeeded);
        Assert.Equal(["trade-first", "trade-same-time", "bar-close", "trade-late"], observed);
        Assert.DoesNotContain("trade-same-time", prefixes["trade-first"]);
        Assert.Contains("trade-first", prefixes["trade-same-time"]);
        Assert.Equal(4, result.Steps.Count);
        Assert.Equal(Utc(2), result.Steps[2].VirtualTime);
        Assert.Equal(Utc(3), result.Steps[3].VirtualTime);
        Assert.Equal(new UtcTimestamp(Time(0)), result.Steps[3].Envelope.CausalEvent.ReceiveTime);
        Assert.Equal(new UtcTimestamp(Time(3)), result.Steps[3].Envelope.CausalEvent.AvailabilityTime);
    }

    [Fact]
    public void RunnerPrefixWaitsForBarCloseAndAvailabilityBoundaries()
    {
        Bar closeAtThree = new("bar-close", Instrument, Utc(1), Utc(0), Utc(3),
            new Price(100m), new Price(102m), new Price(99m), new Price(101m),
            new Quantity(1m));
        Bar availableAtFour = new("bar-late-availability", Instrument, Utc(1), Utc(0),
            Utc(3), new Price(100m), new Price(102m), new Price(99m),
            new Price(101m), new Quantity(1m));
        ReplayEnvelope[] envelopes =
        [
            new("stream-a", new CausalMarketEvent(closeAtThree,
                new UtcTimestamp(Time(1)), new UtcTimestamp(Time(1)), 1,
                new UtcTimestamp(Time(0)))),
            Envelope(availableAtFour, 3, Time(1), Time(4)),
            Envelope(new Trade("trade-before-close", Instrument, Utc(2),
                new Price(101m), new Quantity(1m), TradeSide.Buy), 2, Time(2), Time(2)),
            Envelope(new Trade("trade-at-close", Instrument, Utc(3),
                new Price(101m), new Quantity(1m), TradeSide.Buy), 4, Time(3), Time(3)),
            Envelope(new Trade("trade-at-availability", Instrument, Utc(4),
                new Price(101m), new Quantity(1m), TradeSide.Buy), 5, Time(4), Time(4))
        ];

        Dictionary<string, ReplayStepContext> byEvent = [];
        ReplayRunResult result = HistoricalReplayRunner.Run("stream-a",
            [.. envelopes, envelopes[0]],
            context => byEvent.Add(context.Event.EventId, context));
        Dictionary<string, string[]> permutedPrefixes = [];
        ReplayRunResult permuted = HistoricalReplayRunner.Run("stream-a",
            [.. envelopes.Reverse(), envelopes[0]],
            context => permutedPrefixes.Add(context.Event.EventId, context.Prefix.Events
                .Select(static item => item.CausalEvent.Payload.EventId).ToArray()));

        Assert.True(result.Succeeded);
        Assert.True(permuted.Succeeded);
        Assert.Equal(envelopes.Length, result.Steps.Count);
        Assert.Equal(result.Steps.Select(static step => step.Envelope),
            permuted.Steps.Select(static step => step.Envelope));
        Assert.Contains(result.Diagnostics,
            diagnostic => diagnostic.Kind == ReplayDiagnosticKind.Duplicate);
        Assert.DoesNotContain(byEvent["trade-before-close"].Prefix.Events,
            item => item.CausalEvent.Payload.EventId == "bar-close");
        Assert.Contains(byEvent["trade-at-close"].Prefix.Events,
            item => item.CausalEvent.Payload.EventId == "bar-close");
        Assert.DoesNotContain(byEvent["trade-at-close"].Prefix.Events,
            item => item.CausalEvent.Payload.EventId == "bar-late-availability");
        Assert.Contains(byEvent["trade-at-availability"].Prefix.Events,
            item => item.CausalEvent.Payload.EventId == "bar-late-availability");
        foreach (string eventId in byEvent.Keys)
        {
            Assert.Equal(byEvent[eventId].Prefix.Events.Select(static item =>
                    item.CausalEvent.Payload.EventId),
                permutedPrefixes[eventId]);
        }
        ReplayEnvelope deliveredBar = byEvent["bar-close"].Envelope;
        Assert.Equal(Utc(1), deliveredBar.CausalEvent.Payload.EventTime);
        Assert.Equal(new UtcTimestamp(Time(1)), deliveredBar.CausalEvent.ReceiveTime);
        Assert.Equal(new UtcTimestamp(Time(1)), deliveredBar.CausalEvent.AvailabilityTime);
        Assert.Equal(new UtcTimestamp(Time(0)), deliveredBar.CausalEvent.ExchangeTime);
        Assert.Equal(1, deliveredBar.CausalEvent.StableSequence);
        Assert.Equal(Utc(3), byEvent["bar-close"].VirtualTime);
        Assert.Equal(Utc(4), byEvent["bar-late-availability"].VirtualTime);
    }

    [Fact]
    public void RunnerIsPermutationDeterministicAndRecordsExactDuplicatesOnce()
    {
        ReplayEnvelope first = Envelope(new Trade("trade-1", Instrument, Utc(1),
            new Price(100m), new Quantity(1m), TradeSide.Buy), 1, Time(1), Time(1));
        ReplayEnvelope second = Envelope(new Trade("trade-2", Instrument, Utc(2),
            new Price(101m), new Quantity(1m), TradeSide.Buy), 2, Time(2), Time(2));

        ReplayRunResult ordered = HistoricalReplayRunner.Run(
            "stream-a", [first, second, first], context => context.Event.EventId);
        ReplayRunResult permuted = HistoricalReplayRunner.Run(
            "stream-a", [second, first, first], context => context.Event.EventId);

        Assert.True(ordered.Succeeded);
        Assert.True(permuted.Succeeded);
        Assert.Equal(ordered.Steps.Select(static step => step.Envelope),
            permuted.Steps.Select(static step => step.Envelope));
        Assert.Contains(ordered.Diagnostics,
            diagnostic => diagnostic.Kind == ReplayDiagnosticKind.Duplicate);
        Assert.Equal(2, ordered.Steps.Count);
    }

    [Fact]
    public void RunnerStopsBeforeProcessingConflictingIdentityOrSequence()
    {
        ReplayEnvelope first = Envelope(new Trade("trade-1", Instrument, Utc(1),
            new Price(100m), new Quantity(1m), TradeSide.Buy), 1, Time(1), Time(1));
        ReplayEnvelope conflictingIdentity = Envelope(new Trade("trade-1", Instrument, Utc(1),
            new Price(101m), new Quantity(1m), TradeSide.Buy), 1, Time(1), Time(1));

        int calls = 0;
        ReplayRunResult result = new ReplayRunner().Run(
            "stream-a", [first, conflictingIdentity], _ => calls++);

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Empty(result.Steps);
        Assert.Contains(result.Diagnostics,
            diagnostic => diagnostic.Kind == ReplayDiagnosticKind.IdentityConflict);
    }

    [Fact]
    public void HandlerFailureDoesNotCommitTheFailingStepOrContinue()
    {
        ReplayEnvelope first = Envelope(new Trade("trade-1", Instrument, Utc(1),
            new Price(100m), new Quantity(1m), TradeSide.Buy), 1, Time(1), Time(1));
        ReplayEnvelope second = Envelope(new Trade("trade-2", Instrument, Utc(2),
            new Price(101m), new Quantity(1m), TradeSide.Buy), 2, Time(2), Time(2));

        int calls = 0;
        ReplayRunResult result = new ReplayRunner().Run(
            "stream-a", [first, second], _ =>
            {
                calls++;
                throw new InvalidOperationException("injected");
            });

        Assert.False(result.Succeeded);
        Assert.Equal(1, calls);
        Assert.Empty(result.Steps);
        Assert.Contains(result.Diagnostics,
            diagnostic => diagnostic.Kind == ReplayDiagnosticKind.HandlerFailure);
        Assert.Equal(0, result.FinalCursor.MaxStableSequence);
    }

    private static ReplayEnvelope Envelope(MarketEvent payload, long sequence,
        DateTimeOffset receiveTime, DateTimeOffset availabilityTime) =>
        new("stream-a", new CausalMarketEvent(payload,
            new UtcTimestamp(receiveTime), new UtcTimestamp(availabilityTime), sequence));

    private static DateTimeOffset Time(int minutes) => BaseTime.AddMinutes(minutes);

    private static UtcTimestamp Utc(int minutes) => new(Time(minutes));
}
