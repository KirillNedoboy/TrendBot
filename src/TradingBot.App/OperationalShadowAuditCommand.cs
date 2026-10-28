using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>Offline, read-only export of one operational shadow audit run.</summary>
public static class OperationalShadowAuditCommand
{
    public const string CommandName = "shadow-audit-export";

    public static int Run(IReadOnlyList<string> args, TextWriter? output = null) =>
        RunAsync(args, output).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(IReadOnlyList<string> args,
        TextWriter? output = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        output ??= TextWriter.Null;

        AuditArguments parsed;
        try
        {
            parsed = Parse(args);
        }
        catch (BadArgumentsException exception)
        {
            await WriteStatusAsync(output, "bad-arguments", exception.Message,
                cancellationToken).ConfigureAwait(false);
            return 2;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using OperationalShadowStore store = OperationalShadowStore.OpenReadOnly(
                parsed.DatabasePath);
            OperationalShadowAuditSnapshot snapshot = store.ReadAuditSnapshot(parsed.RunId);
            OperationalShadowAuditEvidence evidence =
                OperationalShadowAuditEvidenceBuilder.Build(snapshot);
            bool created = OperationalShadowAuditExporter.Export(evidence, parsed.OutputPath);
            await WriteStatusAsync(output, created ? "exported" : "unchanged",
                evidence.Identity, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteStatusAsync(output, "export-error", "cancelled",
                CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception)
        {
            await WriteStatusAsync(output, "export-error", exception.Message,
                CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
    }

    private static AuditArguments Parse(IReadOnlyList<string> args)
    {
        int offset = args.Count > 0
            && StringComparer.OrdinalIgnoreCase.Equals(args[0], CommandName) ? 1 : 0;
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        for (int index = offset; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count || !args[index].StartsWith("--",
                    StringComparison.Ordinal)
                || args[index + 1].StartsWith("--", StringComparison.Ordinal)
                || !IsKnownOption(args[index])
                || !values.TryAdd(args[index], args[index + 1]))
            {
                throw new BadArgumentsException("Expected one value for each supported option.");
            }
        }

        if (values.Count != 3
            || !values.TryGetValue("--database", out string? database)
            || !values.TryGetValue("--run-id", out string? runId)
            || !values.TryGetValue("--output", out string? output)
            || string.IsNullOrWhiteSpace(database)
            || string.IsNullOrWhiteSpace(runId)
            || string.IsNullOrWhiteSpace(output))
        {
            throw new BadArgumentsException(
                $"Usage: {CommandName} --database <path> --run-id <id> --output <file>");
        }

        return new AuditArguments(database, runId, output);
    }

    private static bool IsKnownOption(string value) => value is "--database"
        or "--run-id" or "--output";

    private static async Task WriteStatusAsync(TextWriter output, string status, string detail,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync($"{status}: {detail}").ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record AuditArguments(string DatabasePath, string RunId, string OutputPath);

    private sealed class BadArgumentsException(string message) : Exception(message);
}
