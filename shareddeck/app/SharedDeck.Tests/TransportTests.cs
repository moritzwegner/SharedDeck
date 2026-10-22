using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using SharedDeck.Core.Transport;

namespace SharedDeck.Tests;

public class TransportTests
{
    [Fact]
    public async Task BridgeServerExchangesMessagesWithWebSocketClient()
    {
        using var server = new BridgeServer(0);
        var received = new BlockingCollection<string>();
        server.MessageReceived += received.Add;
        server.Start();

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/"), CancellationToken.None);
        await Wait.Until(() => server.IsConnected);

        await client.SendAsync(Encoding.UTF8.GetBytes("{\"ch\":\"bridge\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.True(received.TryTake(out var fromBridge, 5000));
        Assert.Equal("{\"ch\":\"bridge\"}", fromBridge);

        var large = new string('x', 300_000);
        server.Send(large);
        var buffer = new byte[1024 * 1024];
        var total = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), CancellationToken.None);
            total += result.Count;
        } while (!result.EndOfMessage);
        Assert.Equal(large, Encoding.UTF8.GetString(buffer, 0, total));
    }

    [Fact]
    public async Task PeerLinkKeepsOrderAndContent()
    {
        using var host = new PeerLink();
        using var client = new PeerLink();
        var received = new BlockingCollection<string>();
        host.MessageReceived += received.Add;
        host.Listen(0);
        await client.ConnectAsync("127.0.0.1", host.ListenPort, CancellationToken.None);
        await Wait.Until(() => host.IsConnected);

        var random = new Random(42);
        var sent = new List<string>();
        for (var i = 0; i < 2000; i++)
        {
            var message = $"{i}:" + new string((char)('a' + random.Next(26)), random.Next(0, i % 100 == 0 ? 200_000 : 300));
            sent.Add(message);
            client.Send(message);
        }

        foreach (var expected in sent)
        {
            Assert.True(received.TryTake(out var actual, 10_000), "message missing");
            Assert.Equal(expected, actual);
        }
        Assert.True(client.BytesSent > 0);
    }

    [Fact]
    public async Task PeerLinkReportsDisconnect()
    {
        using var host = new PeerLink();
        var client = new PeerLink();
        var disconnected = new TaskCompletionSource<string>();
        host.Disconnected += reason => disconnected.TrySetResult(reason);
        host.Listen(0);
        await client.ConnectAsync("127.0.0.1", host.ListenPort, CancellationToken.None);
        await Wait.Until(() => host.IsConnected);

        client.Dispose();

        var finished = await Task.WhenAny(disconnected.Task, Task.Delay(5000));
        Assert.Same(disconnected.Task, finished);
    }
}

internal static class Wait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
            {
                throw new TimeoutException("condition not met");
            }
            await Task.Delay(10);
        }
    }
}
