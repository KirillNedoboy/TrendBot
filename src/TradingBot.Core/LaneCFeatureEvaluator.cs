using System.Security.Cryptography;
using System.Text;

namespace TradingBot.Core;

/// <summary>Reconstructs captured depth and computes causal, threshold-explicit features.</summary>
public static class LaneCFeatureEvaluator
{
    public static LaneCFeatureResult Evaluate(LaneCFeatureInput input,
        LaneCFeatureConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        if (input.Records.Count > configuration.MaximumInputRecords)
        {
            throw new ArgumentException("The capture input exceeds its configured bound.",
                nameof(input));
        }

        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            CoreJsonSerializer.Serialize(new { Input = input, Configuration = configuration }))))
            .ToLowerInvariant();
        DateTimeOffset windowStart = input.Cursor.AsOf.Value.Subtract(configuration.Window);
        CaptureEnvelope[] eligible = input.Records
            .Where(record => record.StableSequence <= input.Cursor.MaxStableSequence
                && record.EventTime.Value <= input.Cursor.AsOf.Value
                && record.AvailabilityTime.Value <= input.Cursor.AsOf.Value)
            .OrderBy(static record => record.StableSequence)
            .ToArray();
        List<string> reasons = [];
        Dictionary<string, MarketEvent> seenEvents = new(StringComparer.Ordinal);
        Dictionary<decimal, decimal> bids = [];
        Dictionary<decimal, decimal> asks = [];
        List<BookView> bookViews = [];
        List<Trade> trades = [];
        bool bookReady = false;
        bool sequenceGap = false;
        bool conflict = false;
        long lastUpdateId = -1;

        foreach (CaptureEnvelope envelope in eligible)
        {
            if (envelope.Diagnostic is CaptureDiagnostic diagnostic)
            {
                if (IsRecoveryInvalidation(diagnostic.Code))
                {
                    bookReady = false;
                    lastUpdateId = -1;
                    bids.Clear();
                    asks.Clear();
                    bookViews.Clear();
                    sequenceGap = true;
                    AddReason(reasons, diagnostic.Code);
                }

                continue;
            }

            MarketEvent? payload = envelope.Payload;
            if (payload is null)
            {
                continue;
            }

            string eventIdentity = $"{payload.GetType().Name}|{payload.InstrumentId.Value}|{payload.EventId}";
            if (seenEvents.TryGetValue(eventIdentity, out MarketEvent? previousEvent))
            {
                if (previousEvent == payload)
                {
                    AddReason(reasons, "duplicate-event");
                }
                else
                {
                    conflict = true;
                    AddReason(reasons, "event-identity-conflict");
                }

                continue;
            }

            seenEvents.Add(eventIdentity, payload);
            if (payload is Trade trade)
            {
                if (trade.EventTime.Value >= windowStart)
                {
                    trades.Add(trade);
                }

                continue;
            }

            if (payload is not BookDelta delta)
            {
                continue;
            }

            CaptureDepthMetadata? metadata = envelope.Provenance.DepthMetadata;
            if (metadata is null)
            {
                AddReason(reasons, "depth-sequence-metadata-missing");
                continue;
            }

            if (metadata.IsSnapshot)
            {
                bids.Clear();
                asks.Clear();
                ApplySnapshot(delta.Bids, bids);
                ApplySnapshot(delta.Asks, asks);
                lastUpdateId = metadata.FinalUpdateId;
                bookReady = true;
            }
            else if (bookReady)
            {
                if (metadata.FinalUpdateId <= lastUpdateId)
                {
                    AddReason(reasons, "stale-depth-update");
                    continue;
                }

                if (metadata.PreviousUpdateId is long previous
                        && previous != lastUpdateId
                    || metadata.FirstUpdateId > checked(lastUpdateId + 1))
                {
                    bookReady = false;
                    sequenceGap = true;
                    bids.Clear();
                    asks.Clear();
                    bookViews.Clear();
                    lastUpdateId = -1;
                    AddReason(reasons, "depth-sequence-gap");
                    continue;
                }

                ApplyDelta(delta.Bids, bids);
                ApplyDelta(delta.Asks, asks);
                lastUpdateId = metadata.FinalUpdateId;
            }
            else
            {
                AddReason(reasons, "depth-before-ready-snapshot");
                continue;
            }

            bookViews.Add(new BookView(envelope.EventTime, envelope.ReceiveTime,
                envelope.AvailabilityTime, envelope.StableSequence,
                new Dictionary<decimal, decimal>(bids),
                new Dictionary<decimal, decimal>(asks)));
        }

        if (conflict)
        {
            return Empty(LaneCFeatureState.Conflict, "event-identity-conflict");
        }

        if (sequenceGap && !bookReady)
        {
            return Empty(LaneCFeatureState.SequenceGap, "depth-sequence-gap");
        }

        BookView[] windowBooks = bookViews
            .Where(view => view.EventTime.Value >= windowStart)
            .ToArray();
        if (bookReady && bookViews.Count > 0 && windowBooks.Length == 0)
        {
            windowBooks = [bookViews[^1]];
        }

        if (!bookReady || windowBooks.Length == 0)
        {
            return Empty(LaneCFeatureState.InsufficientData, "ready-book-missing");
        }

        BookView current = windowBooks[^1];
        TimeSpan bookAge = input.Cursor.AsOf.Value - current.AvailabilityTime.Value;
        if (bookAge > configuration.MaximumBookAge)
        {
            return Empty(LaneCFeatureState.Stale, "book-stale");
        }

        Trade[] windowTrades = trades
            .Where(trade => trade.EventTime.Value >= windowStart
                && trade.EventTime.Value <= input.Cursor.AsOf.Value)
            .OrderBy(static trade => trade.EventTime.Value)
            .ThenBy(trade => eligible.First(envelope => envelope.Payload == trade).StableSequence)
            .ToArray();
        if (windowTrades.Length > 0)
        {
            CaptureEnvelope latestTradeEnvelope = eligible.Last(envelope =>
                envelope.Payload == windowTrades[^1]);
            if (input.Cursor.AsOf.Value - latestTradeEnvelope.AvailabilityTime.Value
                > configuration.MaximumTradeAge)
            {
                return Empty(LaneCFeatureState.Stale, "trade-stale");
            }
        }

        decimal[] orderedBids = current.Bids.Keys.OrderByDescending(static price => price)
            .ToArray();
        decimal[] orderedAsks = current.Asks.Keys.OrderBy(static price => price).ToArray();
        decimal? bestBid = orderedBids.Length == 0 ? null : orderedBids[0];
        decimal? bestAsk = orderedAsks.Length == 0 ? null : orderedAsks[0];
        decimal? spread = bestBid is decimal bid && bestAsk is decimal ask
            ? ask - bid : null;
        decimal bidDepth = orderedBids.Take(configuration.TopN)
            .Sum(price => current.Bids[price]);
        decimal askDepth = orderedAsks.Take(configuration.TopN)
            .Sum(price => current.Asks[price]);
        decimal depthTotal = checked(bidDepth + askDepth);
        decimal? bookImbalance = depthTotal == 0m
            ? null : checked(bidDepth - askDepth) / depthTotal;
        if (bookImbalance is null)
        {
            AddReason(reasons, "book-imbalance-zero-denominator");
        }

        decimal buyVolume = windowTrades.Where(static trade => trade.Side == TradeSide.Buy)
            .Sum(static trade => trade.Quantity.Value);
        decimal sellVolume = windowTrades.Where(static trade => trade.Side == TradeSide.Sell)
            .Sum(static trade => trade.Quantity.Value);
        decimal totalAggressorVolume = checked(buyVolume + sellVolume);
        decimal? aggressorImbalance = totalAggressorVolume == 0m
            ? null : checked(buyVolume - sellVolume) / totalAggressorVolume;
        if (windowTrades.Length == 0)
        {
            AddReason(reasons, "trade-warmup");
        }

        decimal? midMove = MidMove(windowBooks);
        decimal? response = midMove is decimal move && totalAggressorVolume > 0m
            ? Math.Abs(move) / totalAggressorVolume : null;
        bool? absorptionCandidate = configuration.MinimumAbsorptionVolume is decimal minimum
                && configuration.MaximumAbsorptionPriceMove is decimal maximum
                && midMove is decimal observedMove
            ? totalAggressorVolume >= minimum && Math.Abs(observedMove) <= maximum
            : null;
        if (absorptionCandidate is null)
        {
            AddReason(reasons, "absorption-criteria-unknown");
        }

        LaneCLevelFeature[] levels = BuildLevelFeatures(windowBooks, windowTrades,
            configuration.WallQuantityFloor, out decimal addedBid, out decimal removedBid,
            out decimal addedAsk, out decimal removedAsk,
            out int migrationCandidateCount);
        if (configuration.WallQuantityFloor is null)
        {
            AddReason(reasons, "wall-quantity-floor-unknown");
        }

        LaneCFeatureState state = windowBooks.Length < 2 || windowTrades.Length == 0
            ? LaneCFeatureState.WarmingUp : LaneCFeatureState.Ready;
        if (windowBooks.Length < 2)
        {
            AddReason(reasons, "book-warmup");
        }

        return new LaneCFeatureResult(LaneCFeatureResult.CurrentAlgorithmVersion,
            identity, configuration.FormulaVersion, state, input.Cursor,
            bestBid, bestAsk, spread, bidDepth, askDepth, bookImbalance,
            windowTrades.Length == 0 ? null : buyVolume,
            windowTrades.Length == 0 ? null : sellVolume,
            totalAggressorVolume == 0m ? null : aggressorImbalance,
            addedBid, removedBid, addedAsk, removedAsk, midMove, response,
            absorptionCandidate, migrationCandidateCount,
            new ImmutableValueList<LaneCLevelFeature>(levels),
            new ImmutableValueList<string>(reasons));

        LaneCFeatureResult Empty(LaneCFeatureState state, string reason) =>
            new(LaneCFeatureResult.CurrentAlgorithmVersion, identity,
                configuration.FormulaVersion, state, input.Cursor, null, null, null,
                null, null, null, null, null, null, null, null, null, null,
                null, null, null, 0, new ImmutableValueList<LaneCLevelFeature>([]),
                new ImmutableValueList<string>([reason]));
    }

    private static bool IsRecoveryInvalidation(string code) =>
        code.Contains("gap", StringComparison.OrdinalIgnoreCase)
        || code.Contains("disconnect", StringComparison.OrdinalIgnoreCase)
        || code.Contains("overflow", StringComparison.OrdinalIgnoreCase)
        || code.Contains("snapshotrejected", StringComparison.OrdinalIgnoreCase)
        || code.Contains("recoverylimit", StringComparison.OrdinalIgnoreCase);

    private static void AddReason(List<string> reasons, string reason)
    {
        if (!reasons.Contains(reason, StringComparer.Ordinal))
        {
            reasons.Add(reason);
        }
    }

    private static void ApplySnapshot(IEnumerable<BookLevel> levels,
        Dictionary<decimal, decimal> destination)
    {
        foreach (BookLevel level in levels)
        {
            if (level.Quantity.Value > 0m)
            {
                destination[level.Price.Value] = level.Quantity.Value;
            }
        }
    }

    private static void ApplyDelta(IEnumerable<BookLevel> levels,
        Dictionary<decimal, decimal> destination)
    {
        foreach (BookLevel level in levels)
        {
            if (level.Quantity.Value == 0m)
            {
                destination.Remove(level.Price.Value);
            }
            else
            {
                destination[level.Price.Value] = level.Quantity.Value;
            }
        }
    }

    private static decimal? MidMove(IReadOnlyList<BookView> books)
    {
        if (books.Count < 2)
        {
            return null;
        }

        decimal? firstMid = Mid(books[0]);
        decimal? lastMid = Mid(books[^1]);
        return firstMid is decimal first && lastMid is decimal last
            ? checked(last - first) : null;
    }

    private static decimal? Mid(BookView view)
    {
        decimal? bid = view.Bids.Count == 0 ? null
            : view.Bids.Keys.Max();
        decimal? ask = view.Asks.Count == 0 ? null
            : view.Asks.Keys.Min();
        return bid is decimal bestBid && ask is decimal bestAsk
            ? checked(bestBid + bestAsk) / 2m : null;
    }

    private static LaneCLevelFeature[] BuildLevelFeatures(BookView[] books,
        Trade[] trades, decimal? wallQuantityFloor, out decimal addedBid,
        out decimal removedBid, out decimal addedAsk, out decimal removedAsk,
        out int migrationCandidateCount)
    {
        Dictionary<(LaneCLevelSide Side, decimal Price), LevelTracker> trackers = [];
        decimal addedBidTotal = 0m;
        decimal removedBidTotal = 0m;
        decimal addedAskTotal = 0m;
        decimal removedAskTotal = 0m;
        migrationCandidateCount = 0;
        for (int index = 0; index < books.Length; index++)
        {
            BookView current = books[index];
            BookView? previous = index == 0 ? null : books[index - 1];
            TrackSide(LaneCLevelSide.Bid, current.Bids,
                previous?.Bids ?? EmptyBook(), current, previous);
            TrackSide(LaneCLevelSide.Ask, current.Asks,
                previous?.Asks ?? EmptyBook(), current, previous);

            if (previous is not null && wallQuantityFloor is decimal floor)
            {
                CountMigration(previous.Bids, current.Bids, floor, ref migrationCandidateCount);
                CountMigration(previous.Asks, current.Asks, floor, ref migrationCandidateCount);
            }
        }

        addedBid = addedBidTotal;
        removedBid = removedBidTotal;
        addedAsk = addedAskTotal;
        removedAsk = removedAskTotal;

        return trackers.OrderBy(static pair => pair.Key.Side)
            .ThenBy(static pair => pair.Key.Side == LaneCLevelSide.Bid
                ? -pair.Key.Price : pair.Key.Price)
            .Select(pair =>
            {
                LevelTracker tracker = pair.Value;
                return new LaneCLevelFeature(pair.Key.Side, new Price(pair.Key.Price),
                    tracker.CurrentQuantity, tracker.MaximumQuantity, tracker.ObservedCount,
                    tracker.AddedQuantity, tracker.RemovedQuantity,
                    tracker.ReplenishmentCandidateCount,
                    tracker.WithdrawalCandidateCount,
                    wallQuantityFloor is decimal floor
                        ? tracker.MaximumQuantity >= floor : null);
            })
            .ToArray();

        void TrackSide(LaneCLevelSide side, Dictionary<decimal, decimal> currentLevels,
            Dictionary<decimal, decimal> previousLevels, BookView currentView,
            BookView? previousView)
        {
            HashSet<decimal> prices = currentLevels.Keys.Concat(previousLevels.Keys).ToHashSet();
            foreach (decimal price in prices)
            {
                decimal currentQuantity = currentLevels.GetValueOrDefault(price);
                decimal previousQuantity = previousLevels.GetValueOrDefault(price);
                (LaneCLevelSide Side, decimal Price) key = (side, price);
                if (!trackers.TryGetValue(key, out LevelTracker? tracker))
                {
                    tracker = new LevelTracker();
                    trackers.Add(key, tracker);
                }

                if (currentQuantity > 0m)
                {
                    tracker.ObservedCount++;
                    tracker.MaximumQuantity = Math.Max(tracker.MaximumQuantity, currentQuantity);
                }

                tracker.CurrentQuantity = currentQuantity;
                decimal added = previousView is null
                    ? 0m : Math.Max(0m, currentQuantity - previousQuantity);
                decimal removed = previousView is null
                    ? 0m : Math.Max(0m, previousQuantity - currentQuantity);
                tracker.AddedQuantity = checked(tracker.AddedQuantity + added);
                tracker.RemovedQuantity = checked(tracker.RemovedQuantity + removed);
                if (previousQuantity > 0m && currentQuantity > 0m
                    && currentQuantity > previousQuantity && tracker.RemovedQuantity > 0m)
                {
                    tracker.ReplenishmentCandidateCount++;
                }

                if (previousQuantity > 0m && currentQuantity == 0m
                    && previousView is not null
                    && !trades.Any(trade => trade.Price.Value == price
                        && trade.EventTime.Value >= previousView.EventTime.Value
                        && trade.EventTime.Value <= currentView.EventTime.Value))
                {
                    tracker.WithdrawalCandidateCount++;
                }

                if (side == LaneCLevelSide.Bid)
                {
                    addedBidTotal = checked(addedBidTotal + added);
                    removedBidTotal = checked(removedBidTotal + removed);
                }
                else
                {
                    addedAskTotal = checked(addedAskTotal + added);
                    removedAskTotal = checked(removedAskTotal + removed);
                }
            }
        }
    }

    private static void CountMigration(Dictionary<decimal, decimal> previous,
        Dictionary<decimal, decimal> current, decimal floor, ref int candidates)
    {
        decimal[] removed = previous.Where(pair => pair.Value >= floor
                && current.GetValueOrDefault(pair.Key) == 0m)
            .Select(static pair => pair.Value).OrderBy(static value => value).ToArray();
        decimal[] added = current.Where(pair => pair.Value >= floor
                && previous.GetValueOrDefault(pair.Key) == 0m)
            .Select(static pair => pair.Value).OrderBy(static value => value).ToArray();
        List<decimal> remaining = [.. added];
        foreach (decimal quantity in removed)
        {
            int match = remaining.FindIndex(candidate => candidate == quantity);
            if (match >= 0)
            {
                remaining.RemoveAt(match);
                candidates = checked(candidates + 1);
            }
        }
    }

    private static Dictionary<decimal, decimal> EmptyBook() => [];

    private sealed class LevelTracker
    {
        public decimal CurrentQuantity;
        public decimal MaximumQuantity;
        public int ObservedCount;
        public decimal AddedQuantity;
        public decimal RemovedQuantity;
        public int ReplenishmentCandidateCount;
        public int WithdrawalCandidateCount;
    }

    private sealed record BookView(UtcTimestamp EventTime, UtcTimestamp ReceiveTime,
        UtcTimestamp AvailabilityTime, long StableSequence,
        Dictionary<decimal, decimal> Bids, Dictionary<decimal, decimal> Asks);
}
