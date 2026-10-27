using System.Runtime.CompilerServices;
using System.Text.Json;
using TradingBot.App;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class OperationalShadowCommandTests
{
    [Fact]
    public async Task ShadowCommandHonorsCancellationAndEmitsOnlyJsonZeroOrderSignals()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tradingbot-shadow-command-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite"));
            await using OperationalShadowRuntime runtime = new(
                new DelegateOperationalShadowMarketDataSource(WaitForCancellation), store);
            using CancellationTokenSource cancellation = new();
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));
            using StringWriter output = new();

            int exitCode = await OperationalShadowCommand.RunAsync(runtime, output,
                cancellation.Token);

            Assert.Equal(0, exitCode);
            string[] lines = output.ToString().Split(Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            foreach (string line in lines)
            {
                using JsonDocument document = JsonDocument.Parse(line);
                Assert.Equal("shadow", document.RootElement.GetProperty("mode").GetString());
                Assert.Equal(0, document.RootElement.GetProperty("realOrderCount").GetInt64());
            }

            Assert.Equal("started", JsonDocument.Parse(lines[0]).RootElement
                .GetProperty("status").GetString());
            Assert.Equal("unknown", JsonDocument.Parse(lines[1]).RootElement
                .GetProperty("status").GetString());
            Assert.Equal("stopped", JsonDocument.Parse(lines[2]).RootElement
                .GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnexpectedSourceFailureProducesFaultExitAndStructuredReason()
    {
        string directory = Path.Combine(Path.GetTempPath(), "shadow-command-fault-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite"));
            await using OperationalShadowRuntime runtime = new(
                new DelegateOperationalShadowMarketDataSource(Fail), store);
            using StringWriter output = new();

            int exitCode = await OperationalShadowCommand.RunAsync(runtime, output,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            string[] lines = output.ToString().Split(Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            using JsonDocument fault = JsonDocument.Parse(lines[^1]);
            Assert.Equal("faulted", fault.RootElement.GetProperty("status").GetString());
            Assert.Equal("runtime-fault", fault.RootElement.GetProperty("reason").GetString());
            Assert.Equal(0, fault.RootElement.GetProperty("realOrderCount").GetInt64());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async IAsyncEnumerable<OperationalShadowMarketObservation>
        WaitForCancellation([System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    private static async IAsyncEnumerable<OperationalShadowMarketObservation> Fail(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException("Synthetic source failure.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

