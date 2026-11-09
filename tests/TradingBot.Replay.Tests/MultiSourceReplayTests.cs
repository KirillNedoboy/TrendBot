using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class MultiSourceReplayTests
{
    [Fact]
    public void MultiSourceReplayRunnerIsAvailableAtRuntime()
    {
        Type? runnerType = Type.GetType(
            "TradingBot.Backtesting.MultiSourceReplayRunner, TradingBot.Backtesting");

        Assert.NotNull(runnerType);
    }

    [Fact]
    public void ReplayUsesOneCausalClockAcrossMarketCaptureAndLiquidationSources()
    {
        (MultiSourceDatasetManifest manifest, MultiSourceReplayRecord[] records) = Dataset();
        List<(string EventId, DateTimeOffset Time, string Prefix)> first = Run(records);
        List<(string EventId, DateTimeOffset Time, string Prefix)> permuted =
            Run(records.Reverse().ToArray());

        Assert.Equal(first, permuted);
        Assert.Equal(["trade-1", "liquidation-1", "gap-1", "book-recovered"],
            first.Select(static item => item.EventId));
        Assert.DoesNotContain("gap-1", first[0].Prefix, StringComparison.Ordinal);
        Assert.DoesNotContain("liquidation-1", first[0].Prefix, StringComparison.Ordinal);
        Assert.Equal(Time(5), first[^1].Time);
        Assert.Equal(manifest.Ordering.Count, first.Count);
    }

    [Fact]
    public void ReplayRejectsMissingRecordsAndKeepsExactDuplicateDiagnostic()
    {
        (MultiSourceDatasetManifest manifest, MultiSourceReplayRecord[] records) = Dataset();
        MultiSourceReplayResult missing = MultiSourceReplayRunner.Run(manifest,
            records[..^1], _ => { });
        MultiSourceReplayResult duplicate = MultiSourceReplayRunner.Run(manifest,
            [.. records, records[0]], _ => { });

        Assert.False(missing.Succeeded);
        Assert.Contains(missing.Diagnostics,
            item => item.Kind == MultiSourceReplayDiagnosticKind.MissingRecord);
        Assert.True(duplicate.Succeeded);
        Assert.Equal(records.Length, duplicate.Steps.Count);
        Assert.Contains(duplicate.Diagnostics,
            item => item.Kind == MultiSourceReplayDiagnosticKind.Duplicate);
    }

    [Fact]
    public void ConflictingPayloadForOneManifestReferenceStopsBeforeDelivery()
    {
        (MultiSourceDatasetManifest manifest, MultiSourceReplayRecord[] records) = Dataset();
        Trade changedTrade = new("trade-1", new InstrumentId("BTCUSDT"),
            new UtcTimestamp(Time(1)), new Price(101m), new Quantity(1m), TradeSide.Buy);
        ReplayEnvelope changedEnvelope = new("trades", new CausalMarketEvent(changedTrade,
            new UtcTimestamp(Time(1)), new UtcTimestamp(Time(1)), 0));
        MultiSourceReplayRecord conflict = new(records[0].Ordering, changedEnvelope);
        int delivered = 0;

        MultiSourceReplayResult result = MultiSourceReplayRunner.Run(manifest,
            [.. records, conflict], _ => delivered++);

        Assert.False(result.Succeeded);
        Assert.Equal(0, delivered);
        Assert.Contains(result.Diagnostics,
            item => item.Kind == MultiSourceReplayDiagnosticKind.IdentityConflict);
    }

    [Fact]
    public void EqualEventAndAvailabilityTimesUsePersistedGlobalSequence()
    {
        InstrumentId instrument = new("BTCUSDT");
        MultiSourceReplayRecord[] records =
        [
            Record("trades", "trade-1", new ReplayEnvelope("trades",
                new CausalMarketEvent(new Trade("trade-1", instrument,
                    new UtcTimestamp(Time(1)), new Price(100m), new Quantity(1m),
                    TradeSide.Buy), new UtcTimestamp(Time(1)), new UtcTimestamp(Time(1)), 0)),
                0, 10, Time(1), Time(1)),
            Record("liquidations", "liquidation-1",
                new LaneBLiquidationObservation("liquidations", "liquidation-1", instrument,
                    new UtcTimestamp(Time(1)), new UtcTimestamp(Time(1)),
                    new UtcTimestamp(Time(1)), null, 0, TradeSide.Sell,
                    new Quantity(1m), new Price(99m), "fixed-fixture"),
                0, 20, Time(1), Time(1))
        ];
        Dictionary<string, long[]> prefixes = [];
        MultiSourceReplayResult result = MultiSourceReplayRunner.Run(CreateManifest(records),
            records.Reverse(), context => prefixes.Add(context.Current.Ordering.EventId,
                context.Prefix.Select(static item => item.Ordering.GlobalStableSequence)
                    .ToArray()));

        Assert.True(result.Succeeded);
        Assert.Equal([10L, 20L], result.Steps.Select(static step =>
            step.Current.Ordering.GlobalStableSequence));
        Assert.Equal(20, result.Steps[1].Cursor.MaxStableSequence);
        Assert.Equal([10L], prefixes["trade-1"]);
        Assert.Equal([10L, 20L], prefixes["liquidation-1"]);
    }

    [Fact]
    public void BarCannotEnterTheSharedPrefixBeforeItsCloseTime()
    {
        InstrumentId instrument = new("BTCUSDT");
        Bar bar = new("bar-1", instrument, new UtcTimestamp(Time(1)),
            new UtcTimestamp(Time(1)), new UtcTimestamp(Time(3)), new Price(100m),
            new Price(102m), new Price(99m), new Price(101m), new Quantity(1m));
        MultiSourceReplayRecord[] records =
        [Record("bars", "bar-1", new ReplayEnvelope("bars", new CausalMarketEvent(bar,
            new UtcTimestamp(Time(2)), new UtcTimestamp(Time(2)), 0)), 0, 7,
            Time(1), Time(2))];

        MultiSourceReplayResult result = MultiSourceReplayRunner.Run(CreateManifest(records),
            records, _ => { });

        Assert.True(result.Succeeded);
        Assert.Equal(Time(3), Assert.Single(result.Steps).VirtualTime.Value);
        Assert.Equal(Time(3), result.Steps[0].Cursor.AsOf.Value);
    }

    [Fact]
    public void MultiSourcePrefixWaitsForBarCloseAndAvailabilityBoundaries()
    {
        InstrumentId instrument = new("BTCUSDT");
        Bar closeAtThree = new("bar-close", instrument, new UtcTimestamp(Time(1)),
            new UtcTimestamp(Time(0)), new UtcTimestamp(Time(3)), new Price(100m),
            new Price(102m), new Price(99m), new Price(101m), new Quantity(1m));
        Bar availableAtFour = new("bar-late-availability", instrument,
            new UtcTimestamp(Time(1)), new UtcTimestamp(Time(0)),
            new UtcTimestamp(Time(3)), new Price(100m), new Price(102m),
            new Price(99m), new Price(101m), new Quantity(1m));
        MultiSourceReplayRecord[] records =
        [
            Record("bars", "bar-close", new ReplayEnvelope("bars",
                new CausalMarketEvent(closeAtThree, new UtcTimestamp(Time(1)),
                    new UtcTimestamp(Time(1)), 0, new UtcTimestamp(Time(0)))),
                0, 10, Time(1), Time(1), Time(0)),
            Record("trades", "trade-before-close", new ReplayEnvelope("trades",
                new CausalMarketEvent(new Trade("trade-before-close", instrument,
                    new UtcTimestamp(Time(2)), new Price(101m), new Quantity(1m),
                    TradeSide.Buy), new UtcTimestamp(Time(2)), new UtcTimestamp(Time(2)), 0)),
                0, 20, Time(2), Time(2)),
            Record("bars", "bar-late-availability", new ReplayEnvelope("bars",
                new CausalMarketEvent(availableAtFour, new UtcTimestamp(Time(1)),
                    new UtcTimestamp(Time(4)), 1, new UtcTimestamp(Time(0)))),
                1, 30, Time(1), Time(4), Time(0)),
            Record("trades", "trade-at-close", new ReplayEnvelope("trades",
                new CausalMarketEvent(new Trade("trade-at-close", instrument,
                    new UtcTimestamp(Time(3)), new Price(101m), new Quantity(1m),
                    TradeSide.Buy), new UtcTimestamp(Time(3)), new UtcTimestamp(Time(3)), 1)),
                1, 40, Time(3), Time(3)),
            Record("trades", "trade-at-availability", new ReplayEnvelope("trades",
                new CausalMarketEvent(new Trade("trade-at-availability", instrument,
                    new UtcTimestamp(Time(4)), new Price(101m), new Quantity(1m),
                    TradeSide.Buy), new UtcTimestamp(Time(4)), new UtcTimestamp(Time(4)), 2)),
                2, 50, Time(4), Time(4))
        ];
        MultiSourceDatasetManifest manifest = CreateManifest(records);
        Dictionary<string, MultiSourceReplayStepContext> byEvent = [];
        MultiSourceReplayResult result = MultiSourceReplayRunner.Run(manifest,
            [.. records.Reverse(), records[0]],
            context => byEvent.Add(context.Current.Ordering.EventId, context));
        Dictionary<string, string[]> orderedPrefixes = [];
        MultiSourceReplayResult orderedResult = MultiSourceReplayRunner.Run(manifest,
            records, context => orderedPrefixes.Add(context.Current.Ordering.EventId,
                context.Prefix.Select(static item => item.Ordering.EventId).ToArray()));

        Assert.True(result.Succeeded);
        Assert.True(orderedResult.Succeeded);
        Assert.Equal(records.Length, result.Steps.Count);
        Assert.Contains(result.Diagnostics,
            diagnostic => diagnostic.Kind == MultiSourceReplayDiagnosticKind.Duplicate);
        Assert.Equal(result.Steps.Select(static step => step.Current.Ordering.EventId),
            orderedResult.Steps.Select(static step => step.Current.Ordering.EventId));
        Assert.Equal(result.Steps.Select(static step => step.VirtualTime),
            orderedResult.Steps.Select(static step => step.VirtualTime));
        Assert.DoesNotContain(byEvent["trade-before-close"].Prefix,
            item => item.Ordering.EventId == "bar-close");
        Assert.Contains(byEvent["trade-at-close"].Prefix,
            item => item.Ordering.EventId == "bar-close");
        Assert.DoesNotContain(byEvent["trade-at-close"].Prefix,
            item => item.Ordering.EventId == "bar-late-availability");
        Assert.Contains(byEvent["trade-at-availability"].Prefix,
            item => item.Ordering.EventId == "bar-late-availability");
        foreach (string eventId in byEvent.Keys)
        {
            Assert.Equal(byEvent[eventId].Prefix.Select(static item => item.Ordering.EventId),
                orderedPrefixes[eventId]);
        }
        MultiSourceReplayRecord deliveredBar = byEvent["bar-close"].Current;
        ReplayEnvelope barEnvelope = Assert.IsType<ReplayEnvelope>(deliveredBar.Payload);
        Assert.Equal(0L, deliveredBar.Ordering.SourceSequence);
        Assert.Equal(10L, deliveredBar.Ordering.GlobalStableSequence);
        Assert.Equal(new UtcTimestamp(Time(1)), deliveredBar.Ordering.ReceiveTime);
        Assert.Equal(new UtcTimestamp(Time(0)), deliveredBar.Ordering.ExchangeTime);
        Assert.Equal(new UtcTimestamp(Time(1)), barEnvelope.CausalEvent.Payload.EventTime);
        Assert.Equal(new UtcTimestamp(Time(1)), barEnvelope.CausalEvent.ReceiveTime);
        Assert.Equal(new UtcTimestamp(Time(1)), barEnvelope.CausalEvent.AvailabilityTime);
        Assert.Equal(new UtcTimestamp(Time(0)), barEnvelope.CausalEvent.ExchangeTime);
        Assert.Equal(0, barEnvelope.CausalEvent.StableSequence);
        Assert.Equal(Time(3), byEvent["bar-close"].VirtualTime.Value);
        Assert.Equal(Time(4), byEvent["bar-late-availability"].VirtualTime.Value);
    }

    private static List<(string EventId, DateTimeOffset Time, string Prefix)> Run(
        MultiSourceReplayRecord[] records)
    {
        (MultiSourceDatasetManifest manifest, _) = Dataset();
        List<(string EventId, DateTimeOffset Time, string Prefix)> observed = [];
        MultiSourceReplayResult result = MultiSourceReplayRunner.Run(manifest, records,
            context => observed.Add((context.Current.Ordering.EventId,
                context.VirtualTime.Value, string.Join(",", context.Prefix.Select(static item =>
                    item.Ordering.EventId)))));
        Assert.True(result.Succeeded);
        return observed;
    }

    private static (MultiSourceDatasetManifest Manifest, MultiSourceReplayRecord[] Records)
        Dataset()
    {
        InstrumentId instrument = new("BTCUSDT");
        ReplayEnvelope trade = new("trades", new CausalMarketEvent(
            new Trade("trade-1", instrument, new UtcTimestamp(Time(1)), new Price(100m),
                new Quantity(1m), TradeSide.Buy), new UtcTimestamp(Time(1)),
            new UtcTimestamp(Time(1)), 0));
        LaneBLiquidationObservation liquidation = new("liquidations", "liquidation-1",
            instrument, new UtcTimestamp(Time(1)), new UtcTimestamp(Time(2)),
            new UtcTimestamp(Time(2)), new UtcTimestamp(Time(1)), 0, TradeSide.Sell,
            new Quantity(2m), new Price(99m), "fixed-fixture");
        CaptureEnvelope diagnostic = CaptureEnvelope.FromDiagnostic("l2", 0,
            new UtcTimestamp(Time(1)), new UtcTimestamp(Time(4)), new UtcTimestamp(Time(4)),
            0, new CaptureDiagnostic("gap", "fixed fixture gap", []),
            new CaptureProvenance("fixed-fixture", null, null, "gap-1", null, []));
        CaptureEnvelope recoveredBook = CaptureEnvelope.FromMarketEvent("l2", 1,
            new BookDelta("book-recovered", instrument, new UtcTimestamp(Time(5)),
                [new BookLevel(new Price(99m), new Quantity(1m))],
                [new BookLevel(new Price(101m), new Quantity(1m))]),
            new UtcTimestamp(Time(5)), new UtcTimestamp(Time(5)), 1,
            new CaptureProvenance("fixed-fixture", null, null, "book-recovered", null, []));

        MultiSourceReplayRecord[] records =
        [
            Record("trades", "trade-1", trade, 0, 1, Time(1), Time(1)),
            Record("liquidations", "liquidation-1", liquidation, 0, 2, Time(1), Time(2),
                Time(1)),
            Record("l2", "gap-1", diagnostic, 0, 3, Time(1), Time(4)),
            Record("l2", "book-recovered", recoveredBook, 1, 4, Time(5), Time(5))
        ];
        MultiSourceDatasetManifest manifest = CreateManifest(records);
        return (manifest, records);
    }

    private static MultiSourceDatasetManifest CreateManifest(
        IEnumerable<MultiSourceReplayRecord> records)
    {
        MultiSourceReplayRecord[] snapshot = records.ToArray();
        MultiSourceDatasetSource[] sources = snapshot.GroupBy(static record =>
            record.Ordering.SourceId, StringComparer.Ordinal).Select(group =>
        {
            MultiSourceReplayOrderEntry[] entries = group.Select(static record =>
                record.Ordering).OrderBy(static entry => entry.SourceSequence).ToArray();
            MultiSourceReplayOrderEntry first = entries[0];
            return new MultiSourceDatasetSource(first.SourceId,
                first.SourceId switch
                {
                    "trades" => MultiSourceLaneKind.Trades,
                    "liquidations" => MultiSourceLaneKind.Liquidations,
                    _ => MultiSourceLaneKind.L2
                }, $"manifest-{first.SourceId}", null, [], MultiSourceCoverage.Partial,
                MultiSourceQuality.Ready, [], entries.Length,
                new UtcTimestamp(entries.Min(static entry => entry.EventTime.Value)),
                new UtcTimestamp(entries.Max(static entry => entry.EventTime.Value)),
                new UtcTimestamp(entries.Min(static entry => entry.AvailabilityTime.Value)),
                new UtcTimestamp(entries.Max(static entry => entry.AvailabilityTime.Value)),
                entries[0].SourceSequence, entries[^1].SourceSequence);
        }).ToArray();
        return MultiSourceDatasetManifest.Create("multi-source-replay-fixture", sources,
            snapshot.Select(static item => item.Ordering));
    }

    private static MultiSourceReplayRecord Record(string sourceId, string eventId,
        object payload, long sourceSequence, long globalSequence, DateTimeOffset eventTime,
        DateTimeOffset availabilityTime, DateTimeOffset? exchangeTime = null)
    {
        return new MultiSourceReplayRecord(new MultiSourceReplayOrderEntry(sourceId, eventId,
            sourceSequence, globalSequence, new UtcTimestamp(eventTime),
            exchangeTime is null ? null : new UtcTimestamp(exchangeTime.Value),
            payload switch
            {
                ReplayEnvelope envelope => envelope.CausalEvent.ReceiveTime,
                CaptureEnvelope capture => capture.ReceiveTime,
                ILaneBCausalObservation observation => observation.ReceiveTime,
                _ => throw new ArgumentException("Unsupported fixture payload.", nameof(payload))
            }, new UtcTimestamp(availabilityTime)), payload);
    }

    private static DateTimeOffset Time(int second) =>
        new(2026, 10, 24, 12, 0, second, TimeSpan.Zero);
}
