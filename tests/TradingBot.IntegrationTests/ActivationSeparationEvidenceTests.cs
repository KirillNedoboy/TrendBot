using System.Reflection;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class ActivationSeparationEvidenceTests
{
    private static readonly UtcTimestamp CapturedAt = new(
        new DateTimeOffset(2026, 10, 24, 13, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ClosedHarnessRunRecordsProvenanceAndKeepsRealMoneyGateClosed()
    {
        using TemporaryDatabase database = new();
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        object harness = CreateHarness(store);

        object evidence = BuildEvidence(harness, "p12-m02-t003-fixture",
            "ActivationSeparationEvidenceTests", 7, 7, CapturedAt);

        Assert.Equal(1, Read<int>(evidence, "FormatVersion"));
        Assert.Equal("p12-m02-t003-fixture", Read<string>(evidence, "SourceId"));
        Assert.Equal("ActivationSeparationEvidenceTests",
            Read<string>(evidence, "ProfileName"));
        Assert.Equal(CapturedAt, Read<UtcTimestamp>(evidence, "CapturedAt"));
        Assert.Equal(7, Read<int>(evidence, "PassedTestCount"));
        Assert.Equal(7, Read<int>(evidence, "TotalTestCount"));
        Assert.Equal(0, Read<int>(evidence, "ScriptedDispatchCount"));
        Assert.False(Read<bool>(evidence, "PrivateTransportExposed"));
        Assert.False(Read<bool>(evidence, "CredentialsAccepted"));
        Assert.False(Read<bool>(evidence, "RealMoneyActivationAvailable"));
        Assert.True(Read<bool>(evidence, "ExplicitUserCommandRequired"));
        Assert.Equal("ClosedHarnessOnly", Read<string>(evidence, "GateStatus"));
    }

    [Fact]
    public void EvidenceIdentityIsDeterministicForTheSameClosedRun()
    {
        using TemporaryDatabase database = new();
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        object harness = CreateHarness(store);

        object first = BuildEvidence(harness, "same-source", "same-profile", 3, 3,
            CapturedAt);
        object second = BuildEvidence(harness, "same-source", "same-profile", 3, 3,
            CapturedAt);

        Assert.Equal(Read<string>(first, "EvidenceId"), Read<string>(second, "EvidenceId"));
    }

    [Fact]
    public void EvidenceRequiresACompleteNonzeroSuccessfulProfile()
    {
        using TemporaryDatabase database = new();
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        object harness = CreateHarness(store);

        AssertBuilderArgumentFailure(() => BuildEvidence(harness, "zero", "profile", 0, 0,
            CapturedAt));
        AssertBuilderArgumentFailure(() => BuildEvidence(harness, "partial", "profile", 2, 3,
            CapturedAt));
    }

    [Fact]
    public void BuilderSurfaceCannotReceiveTransportCredentialsOrActivationCommand()
    {
        Type builderType = ResolveBuilderType();
        Type[] forbiddenTypes =
        [
            typeof(HttpClient),
            typeof(Uri),
            typeof(TradingBot.Exchange.Binance.IBinancePrivateRestClient)
        ];
        IEnumerable<Type> surfaceTypes = builderType.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters().Select(parameter =>
                parameter.ParameterType))
            .Concat(builderType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static)
                .SelectMany(method => method.GetParameters().Select(parameter =>
                    parameter.ParameterType)));

        Assert.DoesNotContain(surfaceTypes, type => forbiddenTypes.Contains(type));
        Assert.DoesNotContain(builderType.GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.Name.Contains("Activate", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("EnableLive", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("SubmitReal", StringComparison.OrdinalIgnoreCase));
    }

    private static object CreateHarness(SqlitePortfolioOperationalStore store)
    {
        Type harnessType = typeof(Bootstrap).Assembly.GetType(
            "TradingBot.App.PrivateExecutionDevelopmentHarness")
            ?? throw new InvalidOperationException("The development harness is unavailable.");
        ConstructorInfo constructor = harnessType.GetConstructor(
            [typeof(SqlitePortfolioOperationalStore), typeof(PortfolioId),
                typeof(IReadOnlyList<ExecutionObservation>),
                typeof(IReadOnlyList<ExecutionRecoveryEvidence>)])
            ?? throw new InvalidOperationException("The development harness constructor is unavailable.");
        return constructor.Invoke([
            store,
            new PortfolioId("activation-separation-portfolio"),
            Array.Empty<ExecutionObservation>(),
            Array.Empty<ExecutionRecoveryEvidence>()]);
    }

    private static object BuildEvidence(object harness, string sourceId, string profileName,
        int passedTestCount, int totalTestCount, UtcTimestamp capturedAt)
    {
        Type builderType = ResolveBuilderType();
        MethodInfo method = builderType.GetMethod("Build", BindingFlags.Public | BindingFlags.Static,
            [harness.GetType(), typeof(string), typeof(string), typeof(int), typeof(int),
                typeof(UtcTimestamp)])
            ?? throw new InvalidOperationException("The activation evidence builder is unavailable.");
        return method.Invoke(null,
                   [harness, sourceId, profileName, passedTestCount, totalTestCount, capturedAt])
               ?? throw new InvalidOperationException("The activation evidence builder returned no evidence.");
    }

    private static Type ResolveBuilderType() => typeof(Bootstrap).Assembly.GetType(
        "TradingBot.App.ActivationSeparationEvidenceBuilder")
        ?? throw new InvalidOperationException("The activation evidence builder is not implemented.");

    private static T Read<T>(object evidence, string propertyName) =>
        (T)(evidence.GetType().GetProperty(propertyName)?.GetValue(evidence)
            ?? throw new InvalidOperationException($"Evidence property {propertyName} is unavailable."));

    private static void AssertBuilderArgumentFailure(Action action)
    {
        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(action);
        Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tradingbot-activation-{Guid.NewGuid():N}.sqlite");

        public string Path { get; }

        public void Dispose()
        {
            foreach (string candidate in new[] { Path, $"{Path}-wal", $"{Path}-shm" })
            {
                try
                {
                    File.Delete(candidate);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
