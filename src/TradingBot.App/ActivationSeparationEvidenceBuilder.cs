using System.Security.Cryptography;
using System.Text;
using TradingBot.Core;

namespace TradingBot.App;

/// <summary>
/// Immutable evidence that one successful run stayed inside the sealed
/// development harness. It records facts for review; it cannot authorize a
/// real-money operation.
/// </summary>
public sealed record ActivationSeparationEvidence
{
    public const int CurrentFormatVersion = 1;
    private readonly int _formatVersion = CurrentFormatVersion;
    private readonly bool _privateTransportExposed;
    private readonly bool _credentialsAccepted;
    private readonly bool _realMoneyActivationAvailable;
    private readonly bool _explicitUserCommandRequired;
    private readonly string _gateStatus;

    internal ActivationSeparationEvidence(string evidenceId, string sourceId,
        string profileName, UtcTimestamp capturedAt, int passedTestCount, int totalTestCount,
        int scriptedDispatchCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentNullException.ThrowIfNull(capturedAt);
        if (passedTestCount <= 0 || totalTestCount <= 0 || passedTestCount != totalTestCount)
        {
            throw new ArgumentException(
                "Activation-separation evidence requires a nonzero fully passing profile.");
        }

        if (scriptedDispatchCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scriptedDispatchCount),
                scriptedDispatchCount, "A scripted dispatch count cannot be negative.");
        }

        EvidenceId = evidenceId;
        SourceId = sourceId;
        ProfileName = profileName;
        CapturedAt = capturedAt;
        PassedTestCount = passedTestCount;
        TotalTestCount = totalTestCount;
        ScriptedDispatchCount = scriptedDispatchCount;
        _privateTransportExposed = false;
        _credentialsAccepted = false;
        _realMoneyActivationAvailable = false;
        _explicitUserCommandRequired = true;
        _gateStatus = "ClosedHarnessOnly";
    }

    public int FormatVersion => _formatVersion;
    public string EvidenceId { get; }
    public string SourceId { get; }
    public string ProfileName { get; }
    public UtcTimestamp CapturedAt { get; }
    public int PassedTestCount { get; }
    public int TotalTestCount { get; }
    public int ScriptedDispatchCount { get; }

    // These are fixed properties of the sealed development boundary.
    public bool PrivateTransportExposed => _privateTransportExposed;
    public bool CredentialsAccepted => _credentialsAccepted;
    public bool RealMoneyActivationAvailable => _realMoneyActivationAvailable;
    public bool ExplicitUserCommandRequired => _explicitUserCommandRequired;
    public string GateStatus => _gateStatus;
}

/// <summary>
/// Builds review evidence from a completed scripted harness profile. The
/// input type is sealed and the resulting gate facts are fixed closed values.
/// </summary>
public static class ActivationSeparationEvidenceBuilder
{
    public static ActivationSeparationEvidence Build(
        PrivateExecutionDevelopmentHarness harness, string sourceId, string profileName,
        int passedTestCount, int totalTestCount, UtcTimestamp capturedAt)
    {
        ArgumentNullException.ThrowIfNull(harness);
        RequireLabel(sourceId, nameof(sourceId));
        RequireLabel(profileName, nameof(profileName));
        ArgumentNullException.ThrowIfNull(capturedAt);
        if (totalTestCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalTestCount), totalTestCount,
                "A profile must contain at least one test.");
        }

        if (passedTestCount <= 0 || passedTestCount != totalTestCount)
        {
            throw new ArgumentException(
                "Only a nonzero fully passing profile can produce gate evidence.",
                nameof(passedTestCount));
        }

        string evidenceId = CreateEvidenceId(sourceId, profileName, capturedAt);
        return new ActivationSeparationEvidence(evidenceId, sourceId, profileName, capturedAt,
            passedTestCount, totalTestCount, harness.DispatchCount);
    }

    private static string CreateEvidenceId(string sourceId, string profileName,
        UtcTimestamp capturedAt)
    {
        byte[] input = Encoding.UTF8.GetBytes(
            $"activation-separation-v1\u001f{sourceId}\u001f{profileName}\u001f"
            + capturedAt.Value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static void RequireLabel(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 160 || value.Any(static character =>
            !(char.IsLetterOrDigit(character) || character is '.' or ':' or '/' or '-' or '_')))
        {
            throw new ArgumentException(
                "Evidence provenance labels may contain only bounded identifier characters.",
                parameterName);
        }
    }
}
