using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.App;

/// <summary>Offline validation of a frozen, public-only prospective run envelope.</summary>
public static class SignalOnlyBoundedPreflight
{
    private const int MaximumEnvelopeBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (args.Length != 4 || !StringComparer.Ordinal.Equals(args[0], "signal-only")
            || !StringComparer.Ordinal.Equals(args[1], "preflight")
            || !StringComparer.Ordinal.Equals(args[2], "--envelope")
            || string.IsNullOrWhiteSpace(args[3]))
        {
            await error.WriteLineAsync("Usage: signal-only preflight --envelope <file>").ConfigureAwait(false);
            return 2;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo file = new(args[3]);
            if (file.Length > MaximumEnvelopeBytes)
                throw new InvalidDataException("The envelope exceeds its size bound.");
            byte[] bytes = await File.ReadAllBytesAsync(args[3], cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaximumEnvelopeBytes)
                throw new InvalidDataException("The envelope exceeds its size bound.");
            BoundedSignalEnvelope envelope = Parse(bytes);
            string[] reasons = Validate(envelope, DateTimeOffset.UtcNow);
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = reasons.Length == 0 ? "ready" : "blocked",
                envelopeSha256 = Hash(bytes),
                configurationSha256 = envelope.Configuration is null ? null : Hash(Encoding.UTF8.GetBytes(envelope.ConfigurationJson!)),
                profileSha256 = envelope.Configuration is null ? null : Hash(Encoding.UTF8.GetBytes(
                    CoreJsonSerializer.Serialize(envelope.Configuration.Profile))),
                scenarioPolicySha256 = envelope.Configuration is null ? null : Hash(Encoding.UTF8.GetBytes(
                    CoreJsonSerializer.Serialize(envelope.Configuration.ScenarioPolicies))),
                reasons
            })).ConfigureAwait(false);
            return reasons.Length == 0 ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await output.WriteLineAsync("{\"status\":\"cancelled\"}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException or NotSupportedException
            or OverflowException)
        {
            await error.WriteLineAsync("signal-only preflight: invalid envelope").ConfigureAwait(false);
            return 2;
        }
    }

    internal static BoundedSignalEnvelope Parse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The envelope must be an object.");
        SignalOnlyConfiguration.RejectDuplicateProperties(document.RootElement);
        EnvelopeWire wire = JsonSerializer.Deserialize<EnvelopeWire>(bytes, Options) ?? throw new JsonException();
        if (wire.FormatVersion != 1 || !StringComparer.Ordinal.Equals(wire.Mode, "signal-only-bounded-public"))
            throw new JsonException("The bounded public envelope format is unsupported.");
        string? configurationJson = wire.Configuration.ValueKind == JsonValueKind.Object
            ? wire.Configuration.GetRawText() : null;
        SignalOnlyConfiguration? configuration = configurationJson is null
            ? null : SignalOnlyConfiguration.Parse(configurationJson);
        return new BoundedSignalEnvelope(wire, configuration, configurationJson);
    }

    internal static string[] Validate(BoundedSignalEnvelope envelope, DateTimeOffset now, bool allowExistingRun = false)
    {
        EnvelopeWire wire = envelope.Wire;
        SortedSet<string> reasons = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(wire.RunId)) reasons.Add("run-id-unknown");
        else if (wire.RunId.Length > 80 || wire.RunId.Any(static item =>
            !char.IsAsciiLetterOrDigit(item) && item is not ('-' or '_')))
            reasons.Add("run-id-invalid");
        if (envelope.Configuration is null) reasons.Add("configuration-unknown");
        if (string.IsNullOrWhiteSpace(wire.RunDirectory)) reasons.Add("run-directory-unknown");

        Positive(wire.MaxDurationSeconds, "max-duration-seconds");
        Positive(wire.MaxStoreBytes, "max-store-bytes");
        Positive(wire.MaxWorkingSetBytes, "max-working-set-bytes");
        Positive(wire.MaxLedgerReceipts, "max-ledger-receipts");
        Positive(wire.MaxRestPagesPerTimeframe, "max-rest-pages-per-timeframe");
        Positive(wire.MaxSetupRestRows, "max-setup-rest-rows");
        Positive(wire.MaxContextRestRows, "max-context-rest-rows");
        Positive(wire.MaxHistoryHours, "max-history-hours");
        Positive(wire.MaxReconnectAttempts, "max-reconnect-attempts");
        Positive(wire.MaxReconnectDowntimeSeconds, "max-reconnect-downtime-seconds");
        Positive(wire.MaxFrameBytes, "max-frame-bytes");
        Positive(wire.MaxMessageBytes, "max-message-bytes");
        Positive(wire.MaxRestResponseBytes, "max-rest-response-bytes");
        Positive(wire.RequestTimeoutSeconds, "request-timeout-seconds");
        Positive(wire.FirstSetupCloseDeadlineSeconds, "first-setup-close-deadline-seconds");
        Positive(wire.HealthCheckIntervalSeconds, "health-check-interval-seconds");
        Positive(wire.GracefulStopSeconds, "graceful-stop-seconds");
        Positive(wire.RequiredSetupCloses, "required-setup-closes");
        Positive(wire.RequiredContextCloses, "required-context-closes");
        Positive(wire.OutcomeHorizonSeconds, "outcome-horizon-seconds");
        RequireTrue(wire.StopOnGap, "stop-on-gap");
        RequireTrue(wire.StopOnConflict, "stop-on-conflict");
        RequireTrue(wire.StopOnPersistenceFailure, "stop-on-persistence-failure");
        RequireTrue(wire.RequireRestartParity, "require-restart-parity");
        RequireTrue(wire.RequireFailureDrills, "require-failure-drills");
        if (wire.RequiredDrills is null) reasons.Add("required-drills-unknown");
        else if (wire.RequiredDrills.Length != 5
            || !wire.RequiredDrills.ToHashSet(StringComparer.Ordinal).SetEquals(
                ["disconnect", "gap", "database-failure", "restart", "graceful-stop"]))
            reasons.Add("required-drills-incomplete");
        RequireTrue(wire.RequireNoPrivateOrOrders, "require-no-private-or-orders");
        RequireTrue(wire.RealOutcomesOnly, "real-outcomes-only");
        RequireTrue(wire.AllowNoSetup, "allow-no-setup");
        if (wire.AllowUnknownReady is null) reasons.Add("allow-unknown-ready-unknown");
        else if (wire.AllowUnknownReady.Value) reasons.Add("unknown-readiness-forbidden");
        RequirePolicy(wire.UniversePolicy, "exchange-info-at-run-start", "universe-policy");
        RequirePolicy(wire.OutputMode, "local-journal-and-console", "output-mode");

        if (wire.StartNotBeforeUtc is null) reasons.Add("start-not-before-unknown");
        if (wire.StartNotAfterUtc is null) reasons.Add("start-not-after-unknown");
        if (wire.StartNotBeforeUtc is { } first && wire.StartNotAfterUtc is { } last)
        {
            if (first.Offset != TimeSpan.Zero || last.Offset != TimeSpan.Zero || first >= last)
                reasons.Add("start-window-invalid");
            else if (now < first || now > last)
                reasons.Add("start-window-inactive");
        }
        if (wire.FirstSetupCloseDeadlineSeconds > wire.MaxDurationSeconds)
            reasons.Add("first-close-deadline-exceeds-duration");
        if (wire.MaxReconnectDowntimeSeconds > wire.MaxDurationSeconds
            || wire.GracefulStopSeconds > wire.MaxDurationSeconds
            || wire.RequestTimeoutSeconds > wire.MaxDurationSeconds)
            reasons.Add("timeout-exceeds-duration");
        if (wire.OutcomeHorizonSeconds > wire.MaxDurationSeconds
            || (long?)wire.RequiredSetupCloses * 900 > wire.MaxDurationSeconds
            || (long?)wire.RequiredContextCloses * 3600 > wire.MaxDurationSeconds)
            reasons.Add("acceptance-window-exceeds-duration");
        if (wire.HealthCheckIntervalSeconds > wire.FirstSetupCloseDeadlineSeconds)
            reasons.Add("health-interval-exceeds-deadline");
        if (wire.MaxFrameBytes > wire.MaxMessageBytes)
            reasons.Add("frame-exceeds-message-bound");
        if (wire.MaxRestPagesPerTimeframe is > 0 and { } pages
            && (wire.MaxSetupRestRows > (long)pages * 1500 || wire.MaxContextRestRows > (long)pages * 1500))
            reasons.Add("rest-rows-exceed-page-bound");
        if (wire.MaxStoreBytes is > 0 && wire.MaxRestResponseBytes > wire.MaxStoreBytes)
            reasons.Add("response-exceeds-store-bound");

        if (envelope.Configuration is { } configuration)
        {
            if (!PriceContextFeatureCalculator.IsSupportedAlgorithmVersion(configuration.Profile.Indicators?.PriceContextVersion))
                reasons.Add("price-context-version-unsupported");
            if (!StringComparer.Ordinal.Equals(configuration.Profile.Indicators?.FeatureQualityVersion,
                    FeatureQualityEvaluation.CurrentAlgorithmVersion))
                reasons.Add("feature-quality-version-unsupported");
            if (wire.StartNotAfterUtc is { } latest)
            {
                DateTimeOffset setupStart = configuration.Profile.Setup!.HistoryStartUtc!.Value;
                DateTimeOffset contextStart = configuration.Profile.Context!.HistoryStartUtc!.Value;
                if (setupStart > latest || contextStart > latest)
                    reasons.Add("history-start-after-run-window");
                else
                {
                    if (wire.MaxHistoryHours is > 0
                        && (latest - setupStart > TimeSpan.FromHours(wire.MaxHistoryHours.Value)
                            || latest - contextStart > TimeSpan.FromHours(wire.MaxHistoryHours.Value)))
                        reasons.Add("history-span-exceeds-bound");
                    if (wire.MaxSetupRestRows is > 0
                        && Math.Ceiling((latest - setupStart).TotalSeconds / 900) > wire.MaxSetupRestRows)
                        reasons.Add("setup-history-exceeds-row-bound");
                    if (wire.MaxContextRestRows is > 0
                        && Math.Ceiling((latest - contextStart).TotalSeconds / 3600) > wire.MaxContextRestRows)
                        reasons.Add("context-history-exceeds-row-bound");
                }
            }
            if (!string.IsNullOrWhiteSpace(wire.RunDirectory))
                CheckStorage(configuration, wire.RunDirectory, wire.MaxStoreBytes, reasons, allowExistingRun);
        }
        return reasons.ToArray();

        void Positive(long? value, string name)
        {
            if (value is null) reasons.Add(name + "-unknown");
            else if (value <= 0) reasons.Add(name + "-invalid");
        }
        void RequireTrue(bool? value, string name)
        {
            if (value is null) reasons.Add(name + "-unknown");
            else if (!value.Value) reasons.Add(name + "-required");
        }
        void RequirePolicy(string? value, string required, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) reasons.Add(name + "-unknown");
            else if (!StringComparer.Ordinal.Equals(value, required)) reasons.Add(name + "-unsupported");
        }
    }

    private static void CheckStorage(SignalOnlyConfiguration configuration, string directory,
        long? maximumBytes, SortedSet<string> reasons, bool allowExistingRun)
    {
        string root = Path.GetFullPath(directory);
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!comparer.Equals(configuration.InputLedgerPath, Path.Combine(root, "native-bars.sqlite"))
            || !comparer.Equals(configuration.SignalJournalPath, Path.Combine(root, "signal-journal.sqlite")))
            reasons.Add("storage-path-not-isolated");
        if (File.Exists(root) || !allowExistingRun && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            reasons.Add("storage-not-empty");
        else if (allowExistingRun && !Directory.Exists(root))
            reasons.Add("storage-unavailable");
        DirectoryInfo? ancestor = new(root);
        while (ancestor is not null)
        {
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                reasons.Add("storage-path-not-isolated");
                break;
            }
            ancestor = ancestor.Parent;
        }
        if (maximumBytes is > 0)
        {
            string? volume = Path.GetPathRoot(root);
            if (string.IsNullOrWhiteSpace(volume)) reasons.Add("storage-unavailable");
            else if (new DriveInfo(volume).AvailableFreeSpace < maximumBytes.Value)
                reasons.Add("storage-insufficient");
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal sealed record BoundedSignalEnvelope(EnvelopeWire Wire, SignalOnlyConfiguration? Configuration,
        string? ConfigurationJson);

    internal sealed record EnvelopeWire
    {
        public int? FormatVersion { get; init; }
        public string? Mode { get; init; }
        public string? RunId { get; init; }
        public JsonElement Configuration { get; init; }
        public string? RunDirectory { get; init; }
        public DateTimeOffset? StartNotBeforeUtc { get; init; }
        public DateTimeOffset? StartNotAfterUtc { get; init; }
        public int? MaxDurationSeconds { get; init; }
        public long? MaxStoreBytes { get; init; }
        public long? MaxWorkingSetBytes { get; init; }
        public int? MaxLedgerReceipts { get; init; }
        public int? MaxRestPagesPerTimeframe { get; init; }
        public int? MaxSetupRestRows { get; init; }
        public int? MaxContextRestRows { get; init; }
        public int? MaxHistoryHours { get; init; }
        public int? MaxReconnectAttempts { get; init; }
        public int? MaxReconnectDowntimeSeconds { get; init; }
        public int? MaxFrameBytes { get; init; }
        public int? MaxMessageBytes { get; init; }
        public int? MaxRestResponseBytes { get; init; }
        public int? RequestTimeoutSeconds { get; init; }
        public int? FirstSetupCloseDeadlineSeconds { get; init; }
        public int? HealthCheckIntervalSeconds { get; init; }
        public int? GracefulStopSeconds { get; init; }
        public int? RequiredSetupCloses { get; init; }
        public int? RequiredContextCloses { get; init; }
        public int? OutcomeHorizonSeconds { get; init; }
        public string? UniversePolicy { get; init; }
        public string? OutputMode { get; init; }
        public bool? StopOnGap { get; init; }
        public bool? StopOnConflict { get; init; }
        public bool? StopOnPersistenceFailure { get; init; }
        public bool? RequireRestartParity { get; init; }
        public bool? RequireFailureDrills { get; init; }
        public string[]? RequiredDrills { get; init; }
        public bool? RequireNoPrivateOrOrders { get; init; }
        public bool? RealOutcomesOnly { get; init; }
        public bool? AllowNoSetup { get; init; }
        public bool? AllowUnknownReady { get; init; }
    }
}
