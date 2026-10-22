using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SharedDeck.Core;
using SharedDeck.Core.Transport;

namespace SharedDeck.Tests;

/// <summary>Stands in for the bridge + WASM module of one simulator.</summary>
internal sealed class FakeBridge : IMessageChannel
{
    public readonly ConcurrentQueue<JsonObject> Sent = new();

    public bool IsConnected { get; private set; }
    public event Action<string>? MessageReceived;
    public event Action? Connected;
    public event Action<string>? Disconnected;

    public void Connect(string aircraft = "A32NX", string version = "fork-1")
    {
        IsConnected = true;
        Connected?.Invoke();
        Raise(new JsonObject { ["ch"] = "bridge", ["aircraft"] = aircraft, ["version"] = version });
    }

    public void Disconnect()
    {
        IsConnected = false;
        Disconnected?.Invoke("test");
    }

    public void Send(string message)
    {
        var parsed = (JsonObject)JsonNode.Parse(message)!;
        Sent.Enqueue(parsed);
        // the module answers pings
        if (Wasm(parsed)?["t"]?.GetValue<string>() == "ping")
        {
            RaiseWasm(new JsonObject { ["t"] = "ready", ["v"] = 1 });
        }
    }

    public void Raise(JsonObject message) => MessageReceived?.Invoke(message.ToJsonString());

    public void RaiseWasm(JsonObject payload) => Raise(new JsonObject { ["ch"] = "wasm", ["d"] = payload.ToJsonString() });

    public static JsonObject? Wasm(JsonObject envelope) =>
        envelope["ch"]?.GetValue<string>() == "wasm" ? (JsonObject?)JsonNode.Parse(envelope["d"]!.GetValue<string>()) : null;

    public IEnumerable<JsonObject> SentWasm(string type) =>
        Sent.Select(Wasm).Where(w => w?["t"]?.GetValue<string>() == type).Cast<JsonObject>();

    public IEnumerable<JsonObject> SentChannel(string channel) => Sent.Where(m => m["ch"]?.GetValue<string>() == channel);
}

public class SessionControllerTests : IDisposable
{
    private readonly FakeBridge hostBridge = new();
    private readonly FakeBridge clientBridge = new();
    private readonly SessionController host;
    private readonly SessionController client;

    public SessionControllerTests()
    {
        var port = FreePort();
        var hostConfig = SharedDeckConfig.CreateDefault();
        hostConfig.PeerPort = port;
        var clientConfig = SharedDeckConfig.CreateDefault();
        clientConfig.PeerPort = port;
        clientConfig.KeyDeny.Clear(); // must be replaced by the host's settings
        host = new SessionController(hostConfig, hostBridge, () => new PeerLink());
        client = new SessionController(clientConfig, clientBridge, () => new PeerLink());
        host.LogMessage += line => log.Enqueue("host   " + line);
        client.LogMessage += line => log.Enqueue("client " + line);
    }

    private readonly ConcurrentQueue<string> log = new();

    public void Dispose()
    {
        host.Dispose();
        client.Dispose();
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task ConnectBoth()
    {
        hostBridge.Connect();
        clientBridge.Connect();
        host.StartHost();
        await client.ConnectAsync("127.0.0.1");
        try
        {
            await Wait.Until(() => client.Status.State == SessionState.Synchronizing);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("not synchronizing:\n" + string.Join("\n", log));
        }
    }

    [Fact]
    public async Task ClientTakesHostSettingsAndStartsModules()
    {
        await ConnectBoth();

        Assert.Equal(SessionState.Live, host.Status.State);
        var hostConfig = hostBridge.SentWasm("config").Single();
        Assert.Equal(Protocol.RoleHost, hostConfig["role"]!.GetValue<int>());
        Assert.Single(hostBridge.SentWasm("start"));

        await Wait.Until(() => clientBridge.SentWasm("start").Any());
        var clientConfig = clientBridge.SentWasm("config").Single();
        Assert.Equal(Protocol.RoleClient, clientConfig["role"]!.GetValue<int>());
        Assert.Contains("VIEW*", clientConfig["keyDeny"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(clientConfig["avars"]!.AsArray().Count, hostConfig["avars"]!.AsArray().Count);
    }

    [Fact]
    public async Task FramesFlowToClientAndInputsToHost()
    {
        await ConnectBoth();

        hostBridge.RaiseWasm(new JsonObject { ["t"] = "frame", ["seq"] = 1, ["key"] = true, ["l"] = "" });
        await Wait.Until(() => clientBridge.SentWasm("frame").Any());

        clientBridge.RaiseWasm(new JsonObject { ["t"] = "verify", ["kind"] = "local", ["ok"] = true });
        await Wait.Until(() => client.Status.State == SessionState.Live);

        clientBridge.RaiseWasm(new JsonObject
        {
            ["t"] = "input", ["seq"] = 1,
            ["l"] = new JsonArray(new JsonArray("A32NX_OVHD_ELEC_BAT_1_PB_IS_AUTO", 1)),
            ["k"] = new JsonArray(),
        });
        await Wait.Until(() => hostBridge.SentWasm("input").Any());
        Assert.Equal(1, client.Status.InputsSent);

        clientBridge.RaiseWasm(new JsonObject { ["t"] = "needKeyframe", ["reason"] = "test" });
        await Wait.Until(() => hostBridge.SentWasm("keyframe").Any());
        Assert.NotEmpty(hostBridge.SentChannel("resync"));
    }

    [Fact]
    public async Task StrictDeviationHaltsBothSimulators()
    {
        await ConnectBoth();

        clientBridge.RaiseWasm(new JsonObject
        {
            ["t"] = "verify", ["kind"] = "local", ["ok"] = false,
            ["lvarMismatches"] = new JsonArray("A32NX_TOTAL_FUEL_QUANTITY"),
            ["avarMismatches"] = new JsonArray(), ["contested"] = new JsonArray(),
        });

        await Wait.Until(() => client.Status.State == SessionState.Halted && host.Status.State == SessionState.Halted);
        Assert.Contains("A32NX_TOTAL_FUEL_QUANTITY", client.Status.Reason);
        Assert.Contains(hostBridge.SentWasm("pause"), p => p["on"]!.GetValue<bool>());
        Assert.Contains(clientBridge.SentWasm("pause"), p => p["on"]!.GetValue<bool>());

        client.Resume();
        await Wait.Until(() => host.Status.State == SessionState.Live && client.Status.State == SessionState.Synchronizing);
        Assert.Contains(hostBridge.SentWasm("pause"), p => !p["on"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RepeatedTransportMismatchHalts()
    {
        await ConnectBoth();
        for (var streak = 1; streak <= 3; streak++)
        {
            clientBridge.RaiseWasm(new JsonObject { ["t"] = "verify", ["kind"] = "transport", ["ok"] = false, ["streak"] = streak });
        }
        await Wait.Until(() => host.Status.State == SessionState.Halted);
    }

    [Fact]
    public async Task EventBusIsFilteredPerDirection()
    {
        await ConnectBoth();
        clientBridge.RaiseWasm(new JsonObject { ["t"] = "verify", ["kind"] = "local", ["ok"] = true });
        await Wait.Until(() => client.Status.State == SessionState.Live);

        JsonObject Package(params string[] topics) => new()
        {
            ["ch"] = "eb", ["k"] = "wt.eb.evt",
            ["pkg"] = new JsonObject
            {
                ["busId"] = 1, ["packagedId"] = 2,
                ["data"] = new JsonArray(topics.Select(t => (JsonNode)new JsonObject { ["topic"] = t, ["data"] = 1 }).ToArray()),
            },
        };

        hostBridge.Raise(Package("event_bus", "A32NX_EFIS_L_SYMBOLS"));
        await Wait.Until(() => clientBridge.SentChannel("eb").Any());
        var mirrored = clientBridge.SentChannel("eb").Single()["pkg"]!["data"]!.AsArray();
        Assert.Equal("A32NX_EFIS_L_SYMBOLS", Assert.Single(mirrored)!["topic"]!.GetValue<string>());

        clientBridge.Raise(Package("A32NX_EFIS_R_SYMBOLS", "flightPlanRemoteClient_rpcCommand"));
        await Wait.Until(() => hostBridge.SentChannel("eb").Any());
        var forwarded = hostBridge.SentChannel("eb").Single()["pkg"]!["data"]!.AsArray();
        Assert.Equal("flightPlanRemoteClient_rpcCommand", Assert.Single(forwarded)!["topic"]!.GetValue<string>());
    }

    [Fact]
    public async Task WeatherHintsAndLeverOwnersReachHostWithoutHalt()
    {
        await ConnectBoth();
        clientBridge.RaiseWasm(new JsonObject
        {
            ["t"] = "verify", ["kind"] = "local", ["ok"] = true,
            ["lvarMismatches"] = new JsonArray(), ["avarMismatches"] = new JsonArray(), ["contested"] = new JsonArray(),
            ["advisory"] = new JsonArray(new JsonArray("AMBIENT TEMPERATURE:0", 12.0, 18.0)),
            ["levers"] = new JsonArray(1, 0),
        });

        await Wait.Until(() => host.Status.Hints.Count == 1);
        Assert.Equal(SessionState.Live, client.Status.State);
        Assert.Equal(SessionState.Live, host.Status.State);
        Assert.Contains("AMBIENT TEMPERATURE", host.Status.Hints[0]);
        Assert.Equal("1: Co-Pilot, 2: Kapitän", host.Status.LeverOwners);
    }

    [Fact]
    public async Task ClientLinkLossPausesClient()
    {
        await ConnectBoth();
        host.Stop();
        await Wait.Until(() => client.Status.State == SessionState.LinkLost);
        Assert.Contains(clientBridge.SentWasm("pause"), p => p["on"]!.GetValue<bool>());
    }

    [Fact]
    public async Task HostRejectsDifferentAircraftBuild()
    {
        hostBridge.Connect(version: "fork-1");
        clientBridge.Connect(version: "fork-2");
        host.StartHost();
        await client.ConnectAsync("127.0.0.1");
        await Wait.Until(() => client.Status.State == SessionState.Idle && client.Status.Reason.Contains("Abgelehnt"));
        Assert.Empty(hostBridge.SentWasm("start"));
    }

    [Fact]
    public async Task ClientHaltsWhenHostFramesStop()
    {
        await ConnectBoth();
        await Wait.Until(() => client.Status.State == SessionState.Halted, timeoutMs: 6000);
        Assert.Contains("Keine Daten", client.Status.Reason);
    }
}

public class ProtocolTests
{
    [Theory]
    [InlineData("VIEW*", "VIEW_MODE", true)]
    [InlineData("*CAMERA*", "CHASE_CAMERA_LEFT", true)]
    [InlineData("A32NX_EFB_*", "A32NX_EFB_POWER", true)]
    [InlineData("A32NX_EFB_*", "A32NX_FCU_SPD", false)]
    [InlineData("XMLVAR_*Hidden*", "XMLVAR_YokeHidden1", true)]
    [InlineData("FBW_PILOT_SEAT", "FBW_PILOT_SEAT", true)]
    [InlineData("FBW_PILOT_SEAT", "FBW_PILOT_SEAT_2", false)]
    public void GlobMatches(string pattern, string name, bool expected) =>
        Assert.Equal(expected, Protocol.Matches(new[] { pattern }, name));

    [Fact]
    public void RangesExpandInOrder()
    {
        var expanded = SharedDeckConfig.Expand(new[] { new AVarEntry { Name = "X", Index = 2, IndexTo = 4 } }).Select(a => a.Index);
        Assert.Equal(new[] { 2, 3, 4 }, expanded);
    }

    [Fact]
    public void OldConfigIsUpgradedKeepingUserSettings()
    {
        var path = Path.GetTempFileName();
        try
        {
            var old = SharedDeckConfig.CreateDefault();
            old.ConfigVersion = 1;
            old.LastHostAddress = "copilot-pc";
            old.Strict = false;
            old.LocalAllow.Add("MY_LOCAL_VAR");
            old.AVars.Clear();
            old.Save(path);

            var loaded = SharedDeckConfig.Load(path);

            Assert.Equal(SharedDeckConfig.CurrentVersion, loaded.ConfigVersion);
            Assert.Equal("copilot-pc", loaded.LastHostAddress);
            Assert.False(loaded.Strict);
            Assert.Contains("MY_LOCAL_VAR", loaded.LocalAllow);
            Assert.Contains(loaded.AVars, a => a.Name == "GEAR CENTER POSITION" && a.Setter == "@sc");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClientAppliesHostSettings()
    {
        var host = SharedDeckConfig.CreateDefault();
        host.LocalAllow.Add("TEST_LOCAL");
        host.Strict = false;
        var client = SharedDeckConfig.CreateDefault();
        client.ApplySyncSettings(host.SyncSettings());
        Assert.Contains("TEST_LOCAL", client.LocalAllow);
        Assert.False(client.Strict);
        Assert.Equal(host.AVars.Count, client.AVars.Count);
    }
}
