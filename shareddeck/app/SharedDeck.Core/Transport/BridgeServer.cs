using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace SharedDeck.Core.Transport;

/// <summary>
/// Minimal WebSocket server on localhost for the SharedDeck bridge running inside the simulator.
/// Only one bridge is connected at a time; a new connection replaces the old one (aircraft reload).
/// </summary>
public sealed class BridgeServer : IMessageChannel, IDisposable
{
    private readonly int port;
    private readonly CancellationTokenSource stop = new();
    private TcpListener? listener;
    private Connection? current;

    public BridgeServer(int port) => this.port = port;

    public bool IsConnected => current is { IsOpen: true };

    public event Action<string>? MessageReceived;
    public event Action? Connected;
    public event Action<string>? Disconnected;

    public int Port => ((IPEndPoint?)listener?.LocalEndpoint)?.Port ?? port;

    public void Start()
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = AcceptLoop(stop.Token);
    }

    public void Send(string message) => current?.Enqueue(message);

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener!.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                if (!await Handshake(stream, token))
                {
                    client.Dispose();
                    continue;
                }
                var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(10));
                current?.Close();
                var connection = new Connection(client, socket);
                current = connection;
                connection.MessageReceived += m => MessageReceived?.Invoke(m);
                connection.Closed += reason =>
                {
                    if (ReferenceEquals(current, connection))
                    {
                        current = null;
                        Disconnected?.Invoke(reason);
                    }
                };
                connection.Run(token);
                Connected?.Invoke();
            }
            catch (Exception)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>Reads the HTTP upgrade request and answers with 101 Switching Protocols.</summary>
    internal static async Task<bool> Handshake(Stream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), token);
            if (read == 0)
            {
                return false;
            }
            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            if (text.Contains("\r\n\r\n"))
            {
                var key = text.Split("\r\n")
                    .Select(line => line.Split(':', 2))
                    .Where(parts => parts.Length == 2 && parts[0].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                    .Select(parts => parts[1].Trim())
                    .FirstOrDefault();
                if (key is null)
                {
                    return false;
                }
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                               $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
                return true;
            }
            if (length == buffer.Length)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        current?.Close();
        listener?.Stop();
    }

    private sealed class Connection
    {
        private readonly TcpClient client;
        private readonly WebSocket socket;
        private readonly Channel<string> outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private int closed;

        public Connection(TcpClient client, WebSocket socket)
        {
            this.client = client;
            this.socket = socket;
        }

        public bool IsOpen => closed == 0 && socket.State == WebSocketState.Open;

        public event Action<string>? MessageReceived;
        public event Action<string>? Closed;

        public void Enqueue(string message) => outgoing.Writer.TryWrite(message);

        public void Run(CancellationToken token)
        {
            _ = ReceiveLoop(token);
            _ = SendLoop(token);
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Close("Bridge hat die Verbindung geschlossen");
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                    {
                        MessageReceived?.Invoke(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                        message.SetLength(0);
                    }
                }
            }
            catch (Exception e)
            {
                Close(e.Message);
            }
        }

        private async Task SendLoop(CancellationToken token)
        {
            try
            {
                await foreach (var message in outgoing.Reader.ReadAllAsync(token))
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, token);
                }
            }
            catch (Exception e)
            {
                Close(e.Message);
            }
        }

        public void Close(string reason = "geschlossen")
        {
            if (Interlocked.Exchange(ref closed, 1) == 1)
            {
                return;
            }
            outgoing.Writer.TryComplete();
            try
            {
                client.Dispose();
            }
            catch (Exception)
            {
                // already gone
            }
            Closed?.Invoke(reason);
        }
    }
}
