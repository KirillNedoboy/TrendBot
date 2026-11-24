using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

internal readonly record struct BybitDemoUnavailableStream(int JournalIndex, string StreamId);
internal sealed record BybitDemoSignalBatch(IReadOnlyList<BybitDemoSignalCandidate> Candidates,
    IReadOnlyList<BybitDemoUnavailableStream> UnavailableStreams);

/// <summary>Reads the three separate public journals without writing to them.</summary>
internal static class BybitDemoSignalSource
{
    public static BybitDemoSignalBatch Read(BybitDemoConfiguration configuration,
        IReadOnlySet<string> symbols, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(symbols);
        if (symbols.Count == 0 || now == default || now.Offset != TimeSpan.Zero
            || symbols.Any(static symbol => string.IsNullOrEmpty(symbol) || symbol.Length > 20
                || symbol.Any(static c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))))
        {
            throw new ArgumentException("The demo signal universe or observation time is invalid.");
        }
        Dictionary<string, BybitDemoSignalCandidate> candidates = new(StringComparer.Ordinal);
        List<BybitDemoUnavailableStream> unavailable = [];
        for (int journalIndex = 0; journalIndex < configuration.SignalJournalPaths.Count; journalIndex++)
        {
            using SqliteSignalJournalStore journal = SqliteSignalJournalStore.OpenReadOnly(configuration.SignalJournalPaths[journalIndex]);
            foreach (string symbol in symbols.Order(StringComparer.Ordinal))
            {
                if (journalIndex == 0)
                {
                    ReadStream(journal, journalIndex, symbol, "15m");
                    ReadStream(journal, journalIndex, symbol, "30m");
                }
                else
                {
                    ReadStream(journal, journalIndex, symbol, journalIndex == 1 ? "30m" : "1h");
                }
            }
        }
        return new BybitDemoSignalBatch(candidates.Values.OrderBy(static item => item.DecisionAt)
            .ThenBy(static item => item.SignalId, StringComparer.Ordinal).ToArray(), unavailable.AsReadOnly());

        void ReadStream(SqliteSignalJournalStore journal, int journalIndex, string symbol, string timeframe)
        {
            string streamId = SignalOnlyStreamId.For(symbol, timeframe);
            IReadOnlyList<SignalJournalActive>? activeSignals = ReadAvailableStream(journal, streamId, timeframe, configuration, now);
            if (activeSignals is null)
            {
                unavailable.Add(new BybitDemoUnavailableStream(journalIndex, streamId));
                return;
            }
            foreach (SignalJournalActive active in activeSignals)
            {
                SetupType family = active.Signal.Signal.Identity.Family;
                if (journalIndex == 0 && family is not (SetupType.FlowAtrRecovery or SetupType.FlowStochasticRecovery)
                    || journalIndex != 0 && family != SetupType.DonchianFlowBreakout)
                {
                    continue;
                }
                if (!BybitDemoSignalSelector.TryCreate(active.Signal, active.Episode, active.Alert,
                        symbols, now, TimeSpan.FromSeconds(configuration.MaximumSignalAgeSeconds), out BybitDemoSignalCandidate candidate))
                {
                    continue;
                }
                if (candidates.TryGetValue(candidate.SignalId, out BybitDemoSignalCandidate previous))
                {
                    if (previous != candidate)
                    {
                        throw new InvalidDataException("The public journals contain conflicting signal identities.");
                    }
                }
                else
                {
                    candidates.Add(candidate.SignalId, candidate);
                }
            }
        }
    }

    public static bool IsCurrent(BybitDemoConfiguration configuration, BybitDemoSignalCandidate candidate,
        IReadOnlySet<string> symbols, DateTimeOffset now)
    {
        int index = candidate.Family switch
        {
            SetupType.FlowAtrRecovery when candidate.Timeframe is "15m" or "30m" => 0,
            SetupType.FlowStochasticRecovery when candidate.Timeframe == "15m" => 0,
            SetupType.DonchianFlowBreakout when candidate.Timeframe == "30m" => 1,
            SetupType.DonchianFlowBreakout when candidate.Timeframe == "1h" => 2,
            _ => -1
        };
        if (index < 0 || !symbols.Contains(candidate.Symbol)) return false;
        using SqliteSignalJournalStore journal = SqliteSignalJournalStore.OpenReadOnly(configuration.SignalJournalPaths[index]);
        IReadOnlyList<SignalJournalActive>? activeSignals = ReadAvailableStream(journal,
            SignalOnlyStreamId.For(candidate.Symbol, candidate.Timeframe), candidate.Timeframe, configuration, now);
        if (activeSignals is null) return false;
        foreach (SignalJournalActive active in activeSignals)
            if (BybitDemoSignalSelector.TryCreate(active.Signal, active.Episode, active.Alert, symbols, now,
                TimeSpan.FromSeconds(configuration.MaximumSignalAgeSeconds), out BybitDemoSignalCandidate current)
                && current == candidate) return true;
        return false;
    }

    private static IReadOnlyList<SignalJournalActive>? ReadAvailableStream(SqliteSignalJournalStore journal,
        string streamId, string timeframe, BybitDemoConfiguration configuration, DateTimeOffset now)
    {
        (SignalJournalCheckpoint? checkpoint, IReadOnlyList<SignalJournalActive> activeSignals) = journal.ReadActiveSignals(streamId);
        TimeSpan candle = timeframe switch { "15m" => TimeSpan.FromMinutes(15), "30m" => TimeSpan.FromMinutes(30), _ => TimeSpan.FromHours(1) };
        return checkpoint is null || checkpoint.EventTime.Value > now || checkpoint.AvailabilityTime.Value > now
            || now - checkpoint.EventTime.Value > candle + TimeSpan.FromSeconds(configuration.MaximumSignalAgeSeconds)
            ? null : activeSignals;
    }
}
