using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

internal readonly record struct BybitDemoSignalCandidate(string SignalId, string RecordHash,
    string Symbol, SetupType Family, string Timeframe, Direction Direction, DateTimeOffset DecisionAt,
    decimal ReferencePrice, decimal Stop, decimal FirstTarget, decimal? SecondTarget);

/// <summary>Extracts only selected, still-active committed research scenarios.</summary>
internal static class BybitDemoSignalSelector
{
    public static bool TryCreate(SignalJournalSignal item, SignalScenarioEpisode episode,
        SignalScenarioEvent alert, IReadOnlySet<string> allowedSymbols, DateTimeOffset now,
        TimeSpan maximumAge, out BybitDemoSignalCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(allowedSymbols);
        candidate = default;
        if (now == default || now.Offset != TimeSpan.Zero || maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentException("An explicit UTC time and positive signal age are required.");
        }
        SignalRecord signal = item.Signal;
        signal.ValidateEvidence(item.Evidence);
        SignalIdentity identity = signal.Identity;
        DateTimeOffset decisionAt = signal.DecisionAt.Value;
        if (!StringComparer.Ordinal.Equals(identity.Venue, "BINANCE_USDM")
            || !allowedSymbols.Contains(identity.InstrumentId.Value)
            || !Selected(identity.Family, identity.SetupTimeframe)
            || signal.AssessmentState != SignalAssessmentState.ConfirmedOnClosedBar
            || item.Evidence.State != SignalEvidenceState.Complete
            || episode.Identity != identity || episode.State != SignalScenarioEpisodeState.Active
            || episode.CreatedAt.Value > now || episode.ExpiresAt.Value <= now
            || alert.Kind != SignalScenarioEventKind.AlertCreated
            || !StringComparer.Ordinal.Equals(alert.SignalId, identity.SignalId)
            || !StringComparer.Ordinal.Equals(alert.EventId, episode.AlertEventId)
            || alert.AvailabilityTime.Value < decisionAt || alert.AvailabilityTime.Value > now
            || decisionAt > now || now - decisionAt > maximumAge)
        {
            return false;
        }
        decimal reference = signal.ReferencePrice.Price.Value;
        decimal stop;
        decimal first;
        decimal? second;
        if (identity.Family == SetupType.DonchianFlowBreakout)
        {
            if (!BybitDemoDonchianLevelReader.TryRead(item.Evidence.QualityReasons, identity.Direction,
                    reference, out BybitDemoDonchianLevels channel))
            {
                return false;
            }
            stop = channel.Stop;
            first = channel.Target;
            second = null;
        }
        else if (signal.PriceLevels is
        {
            State: SignalPriceLevelsState.Ready,
            StopLoss: { } stopPrice,
            TakeProfit1: { } firstTarget,
            TakeProfit2: { } secondTarget
        })
        {
            stop = stopPrice;
            first = firstTarget;
            second = secondTarget;
        }
        else
        {
            return false;
        }
        candidate = new BybitDemoSignalCandidate(identity.SignalId, signal.RecordHash,
            identity.InstrumentId.Value, identity.Family, identity.SetupTimeframe,
            identity.Direction, decisionAt, reference, stop, first, second);
        return true;
    }

    private static bool Selected(SetupType family, string timeframe) => family switch
    {
        SetupType.FlowAtrRecovery => timeframe is "15m" or "30m",
        SetupType.FlowStochasticRecovery => timeframe == "15m",
        SetupType.DonchianFlowBreakout => timeframe is "30m" or "1h",
        _ => false
    };
}
