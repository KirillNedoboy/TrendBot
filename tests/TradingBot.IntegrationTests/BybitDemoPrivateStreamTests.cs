using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.App;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoPrivateStreamTests
{
    [Fact]
    public async Task AuthenticatesSubscribesAndPreservesEventsBeforeSubscriptionConfirmation()
    {
        FakeSocket socket = new(
            "{\"success\":true,\"op\":\"auth\"}",
            "{\"topic\":\"execution.linear\",\"data\":[]}",
            "{\"success\":true,\"op\":\"subscribe\"}",
            "{\"op\":\"pong\"}");
        using BybitDemoPrivateStream stream = new(socket);
        await stream.InitializeAsync("test-key", "test-secret", new FixedTimeProvider(), TestContext.Current.CancellationToken);
        using JsonDocument auth = JsonDocument.Parse(socket.Sent[0]);
        JsonElement args = auth.RootElement.GetProperty("args");
        Assert.Equal("auth", auth.RootElement.GetProperty("op").GetString());
        Assert.Equal(1_700_000_010_000, args[1].GetInt64());
        string expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-secret"),
            Encoding.UTF8.GetBytes("GET/realtime1700000010000"))).ToLowerInvariant();
        Assert.Equal(expected, args[2].GetString());
        using JsonDocument subscription = JsonDocument.Parse(socket.Sent[1]);
        Assert.Collection(subscription.RootElement.GetProperty("args").EnumerateArray(),
            item => Assert.Equal("order.linear", item.GetString()),
            item => Assert.Equal("execution.linear", item.GetString()),
            item => Assert.Equal("position.linear", item.GetString()));
        Assert.Contains("execution.linear", Encoding.UTF8.GetString(await stream.ReadAsync(TestContext.Current.CancellationToken)));
        await stream.PingAsync(TestContext.Current.CancellationToken);
        Assert.Equal("{\"op\":\"ping\"}", socket.Sent[2]);
        Assert.Contains("pong", Encoding.UTF8.GetString(await stream.ReadAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("{\"success\":false,\"op\":\"auth\"}")]
    [InlineData("{\"success\":true,\"op\":\"subscribe\"}")]
    public async Task RejectedOrOutOfOrderAuthenticationCannotStartTheStream(string response)
    {
        FakeSocket socket = new(response);
        using BybitDemoPrivateStream stream = new(socket);
        await Assert.ThrowsAsync<InvalidDataException>(() => stream.InitializeAsync(
            "test-key", "test-secret", new FixedTimeProvider(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.ReadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.InitializeAsync(
            "test-key", "test-secret", new FixedTimeProvider(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReceiveFailureAbortsTheSessionAndCannotRemainReady()
    {
        FakeSocket socket = new("{\"success\":true,\"op\":\"auth\"}", "{\"success\":true,\"op\":\"subscribe\"}");
        using BybitDemoPrivateStream stream = new(socket);
        await stream.InitializeAsync("test-key", "test-secret", new FixedTimeProvider(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(WebSocketState.Aborted, socket.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.PingAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
    }

    private sealed class FakeSocket(params string[] responses) : WebSocket
    {
        private readonly Queue<byte[]> _responses = new(responses.Select(Encoding.UTF8.GetBytes));
        private WebSocketState _state = WebSocketState.Open;
        public List<string> Sent { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            byte[] data = _responses.Dequeue();
            data.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(data.Length, WebSocketMessageType.Text, true));
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }
    }
}
