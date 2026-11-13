using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Exchange.Binance;

public enum BinanceKlineSourceKind
{
    Rest,
    WebSocket
}

public enum BinanceKlineIngestionStatus
{
    Accepted,
    Duplicate,
    RevisionConflict,
    Invalid
}

public sealed record BinanceKlineIdentity
{
    public const int CurrentFormatVersion = 2;
    public const string VenueName = "BINANCE";
    public const string ProductName = "USD-M-PERPETUAL";

    public int FormatVersion { get; }

    public string Venue { get; }

    public string Product { get; }

    public string Symbol { get; }

    public string Timeframe { get; }

    public long OpenTimeMilliseconds { get; }

    [JsonIgnore]
    public string EventId =>
        $"bar:v{FormatVersion}:{Venue}:{Product}:{Symbol}:{Timeframe}:{OpenTimeMilliseconds}";

    [JsonConstructor]
    public BinanceKlineIdentity(int formatVersion, string venue, string product, string symbol,
        string timeframe, long openTimeMilliseconds)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                $"Kline identity format {formatVersion} is not supported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(venue);
        ArgumentException.ThrowIfNullOrWhiteSpace(product);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeframe);
        if (!StringComparer.Ordinal.Equals(venue, VenueName)
            || !StringComparer.Ordinal.Equals(product, ProductName)
            || !StringComparer.Ordinal.Equals(symbol, symbol.ToUpperInvariant()))
        {
            throw new ArgumentException("Kline identity fields must use their canonical values.");
        }

        FormatVersion = formatVersion;
        Venue = venue;
        Product = product;
        Symbol = symbol;
        Timeframe = timeframe;
        OpenTimeMilliseconds = openTimeMilliseconds;
    }

    public static BinanceKlineIdentity Create(string symbol, string timeframe,
        long openTimeMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        return new BinanceKlineIdentity(CurrentFormatVersion, VenueName, ProductName,
            symbol.ToUpperInvariant(), timeframe, openTimeMilliseconds);
    }
}

public sealed record BinanceKlineSourceMetadata
{
    public BinanceKlineSourceKind Kind { get; }

    public string SourceId { get; }

    public long? ExchangeEventTimeMilliseconds { get; }

    public UtcTimestamp? ReceiveTime { get; }

    public UtcTimestamp? AvailabilityTime { get; }

    public long? StableSequence { get; }

    public BinanceKlineSourceMetadata(BinanceKlineSourceKind kind, string sourceId,
        long? exchangeEventTimeMilliseconds, UtcTimestamp? receiveTime,
        UtcTimestamp? availabilityTime, long? stableSequence)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (exchangeEventTimeMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exchangeEventTimeMilliseconds));
        }

        if ((receiveTime is null) != (availabilityTime is null))
        {
            throw new ArgumentException(
                "Receive and availability times must either both be present or both be absent.");
        }

        if (receiveTime is not null && availabilityTime!.Value < receiveTime.Value)
        {
            throw new ArgumentException("Availability time cannot precede receive time.",
                nameof(availabilityTime));
        }

        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence));
        }

        Kind = kind;
        SourceId = sourceId;
        ExchangeEventTimeMilliseconds = exchangeEventTimeMilliseconds;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
    }
}

public sealed record BinanceKlineIngestionDiagnostic(string Code, string Message);

public sealed record BinanceKlineAcceptedRevision
{
    public const int CurrentFormatVersion = 1;
    public const string CurrentRevisionAlgorithmVersion = "binance-usdm-kline-revision-v1";

    public int FormatVersion { get; }

    public BinanceKlineIdentity Identity { get; }

    public string RevisionAlgorithmVersion { get; }

    public string RevisionHash { get; }

    public UtcTimestamp InclusiveCloseTime { get; }

    public UtcTimestamp EndExclusiveTime { get; }

    public Bar CoreBar { get; }

    public ImmutableValueList<BinanceKlineSourceMetadata> SourceMetadata { get; }

    internal BinanceKlineAcceptedRevision(BinanceKlineIdentity identity, string revisionHash,
        UtcTimestamp inclusiveCloseTime, UtcTimestamp endExclusiveTime, Bar coreBar,
        IEnumerable<BinanceKlineSourceMetadata> sourceMetadata)
        : this(CurrentFormatVersion, identity, CurrentRevisionAlgorithmVersion, revisionHash,
            inclusiveCloseTime, endExclusiveTime, coreBar, sourceMetadata)
    {
    }

    private BinanceKlineAcceptedRevision(int formatVersion, BinanceKlineIdentity identity,
        string revisionAlgorithmVersion, string revisionHash, UtcTimestamp inclusiveCloseTime,
        UtcTimestamp endExclusiveTime, Bar coreBar,
        IEnumerable<BinanceKlineSourceMetadata> sourceMetadata)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(formatVersion, CurrentFormatVersion);


        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionAlgorithmVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionHash);
        ArgumentNullException.ThrowIfNull(inclusiveCloseTime);
        ArgumentNullException.ThrowIfNull(endExclusiveTime);
        ArgumentNullException.ThrowIfNull(coreBar);
        ArgumentNullException.ThrowIfNull(sourceMetadata);

        FormatVersion = formatVersion;
        Identity = identity;
        RevisionAlgorithmVersion = revisionAlgorithmVersion;
        RevisionHash = revisionHash;
        InclusiveCloseTime = inclusiveCloseTime;
        EndExclusiveTime = endExclusiveTime;
        CoreBar = coreBar;
        SourceMetadata = new ImmutableValueList<BinanceKlineSourceMetadata>(
            sourceMetadata.Distinct());
    }

    internal BinanceKlineAcceptedRevision WithSourceMetadata(
        BinanceKlineSourceMetadata sourceMetadata)
    {
        if (SourceMetadata.Contains(sourceMetadata))
        {
            return this;
        }

        return new BinanceKlineAcceptedRevision(FormatVersion, Identity,
            RevisionAlgorithmVersion, RevisionHash, InclusiveCloseTime, EndExclusiveTime,
            CoreBar, SourceMetadata.Append(sourceMetadata));
    }
}

public sealed record BinanceKlineIngestionResult
{
    public BinanceKlineIngestionStatus Status { get; }

    public BinanceKlineIdentity Identity { get; }

    public BinanceKlineSourceMetadata SourceMetadata { get; }

    public string? RevisionHash { get; }

    public BinanceKlineAcceptedRevision? AcceptedRevision { get; }

    public UtcTimestamp? InclusiveCloseTime { get; }

    public UtcTimestamp? EndExclusiveTime { get; }

    public BinanceKlineIngestionDiagnostic? Diagnostic { get; }

    public Bar? CoreBar { get; }

    internal BinanceKlineIngestionResult(BinanceKlineIngestionStatus status,
        BinanceKlineIdentity identity, BinanceKlineSourceMetadata sourceMetadata,
        string? revisionHash, BinanceKlineAcceptedRevision? acceptedRevision,
        UtcTimestamp? inclusiveCloseTime, UtcTimestamp? endExclusiveTime,
        BinanceKlineIngestionDiagnostic? diagnostic, Bar? coreBar)
    {
        Status = status;
        Identity = identity;
        SourceMetadata = sourceMetadata;
        RevisionHash = revisionHash;
        AcceptedRevision = acceptedRevision;
        InclusiveCloseTime = inclusiveCloseTime;
        EndExclusiveTime = endExclusiveTime;
        Diagnostic = diagnostic;
        CoreBar = coreBar;
    }
}

public static class BinanceKlineIngestionBoundary
{
    public static BinanceKlineIngestionResult Evaluate(string symbol, BinanceKline kline,
        BinanceKlineSourceMetadata sourceMetadata,
        BinanceKlineAcceptedRevision? acceptedRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(kline);
        ArgumentNullException.ThrowIfNull(sourceMetadata);

        BinanceKlineIdentity identity = BinanceKlineIdentity.Create(symbol, kline.Timeframe,
            kline.OpenTimeMilliseconds);
        if (!PublicSignalTimeframes.TryGetDurationMilliseconds(kline.Timeframe,
                out long durationMilliseconds))
        {
            return Invalid(identity, sourceMetadata, "unsupported-timeframe",
                "The native UTC interval is unsupported.");
        }

        if (kline.OpenTimeMilliseconds < 0)
        {
            return Invalid(identity, sourceMetadata, "invalid-open-time",
                "Kline open time must be a non-negative UTC Unix-millisecond timestamp.");
        }

        if (kline.OpenTimeMilliseconds % durationMilliseconds != 0)
        {
            return Invalid(identity, sourceMetadata, "open-time-off-utc-grid",
                "Kline open time is not aligned to its native UTC interval grid.");
        }

        if (!TryGetEndExclusive(kline.OpenTimeMilliseconds, durationMilliseconds,
                out long endExclusiveMilliseconds))
        {
            return Invalid(identity, sourceMetadata, "invalid-open-time",
                "Kline open time cannot form a representable native interval.");
        }

        long expectedInclusiveCloseMilliseconds = endExclusiveMilliseconds - 1;
        if (kline.CloseTimeMilliseconds != expectedInclusiveCloseMilliseconds)
        {
            return Invalid(identity, sourceMetadata, "interval-duration-mismatch",
                "Raw inclusive close time does not match the native interval duration.");
        }

        if (!TryCreateTimestamp(kline.OpenTimeMilliseconds, out UtcTimestamp? openTime))
        {
            return Invalid(identity, sourceMetadata, "invalid-open-time",
                "Kline open time is outside the UTC timestamp range.");
        }

        if (!TryCreateTimestamp(kline.CloseTimeMilliseconds, out UtcTimestamp? inclusiveCloseTime))
        {
            return Invalid(identity, sourceMetadata, "invalid-inclusive-close-time",
                "Raw inclusive close time is outside the UTC timestamp range.");
        }

        if (!TryCreateTimestamp(endExclusiveMilliseconds, out UtcTimestamp? endExclusiveTime))
        {
            return Invalid(identity, sourceMetadata, "invalid-end-exclusive-time",
                "End-exclusive time is outside the UTC timestamp range.");
        }

        if (!kline.IsClosed)
        {
            return Invalid(identity, sourceMetadata, "bar-not-closed",
                "An open kline cannot become Core-ready input.");
        }

        string revisionHash = ComputeRevisionHash(kline);
        if (acceptedRevision is not null && acceptedRevision.Identity == identity)
        {
            return CompareWithAcceptedRevision(identity, sourceMetadata, revisionHash,
                acceptedRevision, inclusiveCloseTime!, endExclusiveTime!);
        }

        Bar bar = new(identity.EventId, new InstrumentId(identity.Symbol), endExclusiveTime!,
            openTime!, endExclusiveTime!, new Price(kline.Open), new Price(kline.High),
            new Price(kline.Low), new Price(kline.Close), new Quantity(kline.Volume));
        BinanceKlineAcceptedRevision accepted = new(identity, revisionHash,
            inclusiveCloseTime!, endExclusiveTime!, bar, [sourceMetadata]);
        return new BinanceKlineIngestionResult(BinanceKlineIngestionStatus.Accepted,
            identity, sourceMetadata, revisionHash, accepted, inclusiveCloseTime,
            endExclusiveTime, null, bar);
    }

    private static BinanceKlineIngestionResult CompareWithAcceptedRevision(
        BinanceKlineIdentity identity, BinanceKlineSourceMetadata sourceMetadata,
        string revisionHash, BinanceKlineAcceptedRevision acceptedRevision,
        UtcTimestamp inclusiveCloseTime, UtcTimestamp endExclusiveTime)
    {
        if (!StringComparer.Ordinal.Equals(acceptedRevision.RevisionAlgorithmVersion,
                BinanceKlineAcceptedRevision.CurrentRevisionAlgorithmVersion))
        {
            return Invalid(identity, sourceMetadata, "revision-algorithm-unknown",
                "Accepted revision uses an unsupported revision-hash algorithm.");
        }

        if (!StringComparer.Ordinal.Equals(acceptedRevision.RevisionHash, revisionHash))
        {
            return new BinanceKlineIngestionResult(BinanceKlineIngestionStatus.RevisionConflict,
                identity, sourceMetadata, revisionHash, acceptedRevision, inclusiveCloseTime,
                endExclusiveTime, new BinanceKlineIngestionDiagnostic("revision-conflict",
                    "A different content hash was received for the accepted kline identity."),
                null);
        }

        BinanceKlineAcceptedRevision updated =
            acceptedRevision.WithSourceMetadata(sourceMetadata);
        return new BinanceKlineIngestionResult(BinanceKlineIngestionStatus.Duplicate, identity,
            sourceMetadata, revisionHash, updated, inclusiveCloseTime, endExclusiveTime, null,
            null);
    }

    private static BinanceKlineIngestionResult Invalid(BinanceKlineIdentity identity,
        BinanceKlineSourceMetadata sourceMetadata, string code, string message) =>
        new(BinanceKlineIngestionStatus.Invalid, identity, sourceMetadata, null, null,
            null, null, new BinanceKlineIngestionDiagnostic(code, message), null);


    private static bool TryGetEndExclusive(long openTimeMilliseconds,
        long durationMilliseconds, out long endExclusiveMilliseconds)
    {
        try
        {
            endExclusiveMilliseconds = checked(openTimeMilliseconds + durationMilliseconds);
            return true;
        }
        catch (OverflowException)
        {
            endExclusiveMilliseconds = 0;
            return false;
        }
    }

    private static bool TryCreateTimestamp(long milliseconds, out UtcTimestamp? timestamp)
    {
        try
        {
            timestamp = new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            timestamp = null;
            return false;
        }
    }

    private static string ComputeRevisionHash(BinanceKline kline)
    {
        KlineRevisionContent content = new(
            BinanceKlineAcceptedRevision.CurrentRevisionAlgorithmVersion,
            kline.OpenTimeMilliseconds, kline.CloseTimeMilliseconds,
            CanonicalDecimal(kline.Open), CanonicalDecimal(kline.High),
            CanonicalDecimal(kline.Low), CanonicalDecimal(kline.Close),
            CanonicalDecimal(kline.Volume), CanonicalDecimal(kline.QuoteAssetVolume),
            kline.TradeCount, CanonicalDecimal(kline.TakerBuyBaseVolume),
            CanonicalDecimal(kline.TakerBuyQuoteVolume));
        byte[] bytes = Encoding.UTF8.GetBytes(CoreJsonSerializer.Serialize(content));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string CanonicalDecimal(decimal value) =>
        value.ToString("G29", CultureInfo.InvariantCulture);

    private sealed record KlineRevisionContent(string AlgorithmVersion,
        long OpenTimeMilliseconds, long InclusiveCloseTimeMilliseconds, string Open,
        string High, string Low, string Close, string Volume, string QuoteAssetVolume,
        long TradeCount, string TakerBuyBaseVolume, string TakerBuyQuoteVolume);
}
