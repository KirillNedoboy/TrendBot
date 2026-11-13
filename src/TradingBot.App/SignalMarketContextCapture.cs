using System.Text.Json;
using TradingBot.Core;

namespace TradingBot.App;

internal static class SignalMarketContextCapture
{
    internal static SignalMarketContext FromDecision(NativePublicKlineEvaluation evaluation,
        SignalMultiLaneInput input, ISignalMarketContextSource source, decimal binSize)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(source);
        LaneCFeatureInput? history = input.MicrostructureInput;
        UtcTimestamp? coverage = history?.Cursor.AsOf == input.DecisionAt
            ? input.TradeCoverageStart : null;
        SignalObservedPoc poc = SignalMarketContextCalculator.CalculatePoc(input.DecisionAt,
            input.Policy.Microstructure.Window, binSize, coverage, VisibleTrades(history));
        return Create(evaluation, input.DecisionAt, poc, source.CaptureLiquidation(input.DecisionAt));
    }

    internal static SignalMarketContext FromLive(NativePublicKlineEvaluation evaluation,
        ISignalMarketContextSource source, decimal binSize, UtcTimestamp asOf)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(asOf);
        return Create(evaluation, asOf, source.CapturePoc(asOf, binSize),
            source.CaptureLiquidation(asOf));
    }

    private static SignalMarketContext Create(NativePublicKlineEvaluation evaluation,
        UtcTimestamp asOf, SignalObservedPoc poc, SignalObservedLiquidation? liquidation)
    {
        SignalSessionVwap vwap = SignalMarketContextCalculator.CalculateSessionVwap(
            evaluation.Input.SetupTimeframe, evaluation.TriggerRevision.CoreBar.CloseTime,
            asOf, evaluation.Input.SetupBars);
        return SignalMarketContext.Create(evaluation.Input.InstrumentId, asOf, vwap, poc, liquidation);
    }

    private static IEnumerable<SignalContextTrade> VisibleTrades(LaneCFeatureInput? history)
    {
        if (history is null)
        {
            yield break;
        }
        foreach (CaptureEnvelope row in history.Records)
        {
            if (row.StableSequence <= history.Cursor.MaxStableSequence && row.Payload is Trade trade)
            {
                yield return new SignalContextTrade(trade.Price.Value, trade.Quantity.Value,
                    trade.EventTime.Value, row.AvailabilityTime.Value,
                    SourceEventTime(row.Provenance.RawPayload));
            }
        }
    }

    private static DateTimeOffset? SourceEventTime(string? rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(rawPayload);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (root.TryGetProperty("data", out JsonElement data))
            {
                root = data;
            }
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("E", out JsonElement eventTime)
                && eventTime.TryGetInt64(out long milliseconds)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentOutOfRangeException
            or InvalidOperationException)
        {
            return null;
        }
    }
}
