using System.Text.Json.Nodes;
using TradingBot.App;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoServiceApprovalTests
{
    [Fact]
    public void PendingMalformedAndRevokedApprovalsNeverRetainAnEarlierGrant()
    {
        using Fixture fixture = new();
        BybitDemoServiceApproval approval = fixture.Create();
        Assert.False(approval.IsAuthorized(fixture.Now));
        File.WriteAllText(fixture.Receipt, approval.PendingRequestJson());
        Assert.False(approval.IsAuthorized(fixture.Now));
        fixture.Grant(approval);
        Assert.True(approval.IsAuthorized(fixture.Now));
        File.WriteAllText(fixture.Receipt, "{\"formatVersion\":");
        Assert.False(approval.IsAuthorized(fixture.Now));
        fixture.Grant(approval);
        Assert.True(approval.IsAuthorized(fixture.Now));
        File.Delete(fixture.Receipt);
        Assert.False(approval.IsAuthorized(fixture.Now));
    }

    [Theory]
    [InlineData("config")]
    [InlineData("profile")]
    [InlineData("release")]
    [InlineData("credential")]
    [InlineData("external-signals")]
    public void ChangedExecutionScopeCannotReuseAServiceGrant(string change)
    {
        using Fixture fixture = new();
        fixture.Grant(fixture.Create());
        BybitDemoConfiguration configuration = fixture.Configuration;
        SignalOnlyConfiguration[] profiles = [.. fixture.Profiles];
        if (change == "config")
        {
            JsonObject json = JsonNode.Parse(configuration.ToJson())!.AsObject();
            json["targetNotionalUsdt"] = 200;
            configuration = BybitDemoConfiguration.Parse(json.ToJsonString());
        }
        if (change == "profile")
        {
            JsonObject json = JsonNode.Parse(profiles[0].ToJson())!.AsObject();
            json["signalJournalPath"] = Path.Combine(fixture.Directory, "different-input.sqlite");
            profiles[0] = SignalOnlyConfiguration.Parse(json.ToJsonString());
        }
        if (change == "release") File.WriteAllText(Path.Combine(fixture.Release, "module.bin"), "changed-executable");
        BybitDemoServiceApproval changed = BybitDemoServiceApproval.Create(fixture.Receipt, configuration, profiles,
            change == "credential" ? "different-synthetic-key" : "synthetic-key", change == "external-signals", fixture.Release);
        Assert.False(changed.IsAuthorized(fixture.Now));
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("future")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    public void InvalidReceiptCannotAuthorizeEntries(string change)
    {
        using Fixture fixture = new();
        BybitDemoServiceApproval approval = fixture.Create();
        JsonObject json = fixture.Grant(approval);
        if (change == "mode") json["mode"] = "production";
        if (change == "future") json["authorizedAtUtc"] = fixture.Now.AddDays(1);
        string text = json.ToJsonString();
        if (change == "duplicate") text = "{\"formatVersion\":1," + text[1..];
        if (change == "oversized") text += new string(' ', 8192);
        File.WriteAllText(fixture.Receipt, text);
        Assert.False(approval.IsAuthorized(fixture.Now));
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "bybit-approval-" + Guid.NewGuid().ToString("N"));
        public string Release => Path.Combine(Directory, "release");
        public string Receipt => Path.Combine(Directory, "approval.json");
        public DateTimeOffset Now { get; } = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        public BybitDemoConfiguration Configuration { get; }
        public SignalOnlyConfiguration[] Profiles { get; }
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Release);
            File.WriteAllText(Path.Combine(Release, "module.bin"), "synthetic-executable");
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            Configuration = BybitDemoConfiguration.Parse(File.ReadAllText(Path.Combine(root, "bybit-demo.example.json")));
            Profiles = [SignalOnlyConfiguration.Load(Path.Combine(root, "signal-only.flow-25.example.json")),
                SignalOnlyConfiguration.Load(Path.Combine(root, "signal-only.donchian-flow-30m.example.json")),
                SignalOnlyConfiguration.Load(Path.Combine(root, "signal-only.donchian-flow.example.json"))];
        }
        public BybitDemoServiceApproval Create() => BybitDemoServiceApproval.Create(Receipt, Configuration, Profiles, "synthetic-key", false, Release);
        public JsonObject Grant(BybitDemoServiceApproval approval)
        {
            JsonObject json = JsonNode.Parse(approval.PendingRequestJson())!.AsObject();
            json["statement"] = "ENABLE BYBIT DEMO SERVICE";
            json["authorizedAtUtc"] = Now;
            File.WriteAllText(Receipt, json.ToJsonString());
            return json;
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
