using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace SharedDeck.Core.Transport;

/// <summary>
/// Connection between host and client. TCP keeps every message, in order - a lost or reordered frame
/// would break the state, so reliability is preferred over the last bit of latency. Messages are
/// length-prefixed and the whole stream is deflate-compressed (flushed after every message).
/// </summary>
public sealed class PeerLink : IPeerConnector
{
    private const int MaxMessageSize = 64 * 1024 * 1024;

    private readonly CancellationTokenSource stop = new();
    private TcpListener? listener;
    private TcpClient? client;
    private Channel<string>? outgoing;
    private int connectionId;

    public bool IsConnected => client?.Connected == true && outgoing != null;

    public event Action<string>? MessageReceived;
    public event Action? Connected;
    public event Action<string>? Disconnected;

    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }

    /// <summary>Host side: accepts one client at a time.</summary>
    public void Listen(int port)
    {
        listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();
        _ = AcceptLoop(stop.Token);
    }

    public int ListenPort => ((IPEndPoint?)listener?.LocalEndpoint)?.Port ?? 0;

    /// <summary>Client side.</summary>
    public async Task ConnectAsync(string host, int port, CancellationToken token)
    {
        var tcp = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        tcp.Client.DualMode = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await tcp.ConnectAsync(host, port, timeout.Token);
        Attach(tcp);
    }

    public void Send(string message) => outgoing?.Writer.TryWrite(message);

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var tcp = await listener!.AcceptTcpClientAsync(token);
                tcp.NoDelay = true;
                if (IsConnected)
                {
                    // only one co-pilot
                    tcp.Dispose();
                    continue;
                }
                Attach(tcp);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // keep listening
            }
        }
    }

    private void Attach(TcpClient tcp)
    {
        var id = Interlocked.Increment(ref connectionId);
        client = tcp;
        var channel = Channel.CreateUnbounded<string>();
        outgoing = channel;
        var stream = tcp.GetStream();
        _ = ReceiveLoop(id, stream, stop.Token);
        _ = SendLoop(id, stream, channel, stop.Token);
        Connected?.Invoke();
    }

    private async Task SendLoop(int id, NetworkStream stream, Channel<string> channel, CancellationToken token)
    {
        try
        {
            var counting = new CountingStream(stream, n => BytesSent += n);
            await using var output = new DeflateStream(counting, CompressionLevel.Fastest, leaveOpen: true);
            var header = new byte[4];
            await foreach (var message in channel.Reader.ReadAllAsync(token))
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
                await output.WriteAsync(header, token);
                await output.WriteAsync(bytes, token);
                // send immediately unless more messages are already waiting
                if (channel.Reader.Count == 0)
                {
                    await output.FlushAsync(token);
                }
            }
        }
        catch (Exception e)
        {
            Drop(id, e.Message);
        }
    }

    private async Task ReceiveLoop(int id, NetworkStream stream, CancellationToken token)
    {
        try
        {
            var counting = new CountingStream(stream, n => BytesReceived += n);
            await using var input = new DeflateStream(counting, CompressionMode.Decompress, leaveOpen: true);
            var header = new byte[4];
            while (!token.IsCancellationRequested)
            {
                await input.ReadExactlyAsync(header, token);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length < 0 || length > MaxMessageSize)
                {
                    throw new InvalidDataException($"invalid message length {length}");
                }
                var bytes = new byte[length];
                await input.ReadExactlyAsync(bytes, token);
                MessageReceived?.Invoke(Encoding.UTF8.GetString(bytes));
            }
        }
        catch (EndOfStreamException)
        {
            Drop(id, "Gegenstelle hat die Verbindung beendet");
        }
        catch (Exception e)
        {
            Drop(id, e.Message);
        }
    }

    private void Drop(int id, string reason)
    {
        if (Interlocked.CompareExchange(ref connectionId, id + 1, id) != id)
        {
            return;
        }
        outgoing?.Writer.TryComplete();
        outgoing = null;
        try
        {
            client?.Dispose();
        }
        catch (Exception)
        {
            // already closed
        }
        client = null;
        Disconnected?.Invoke(reason);
    }

    public void Close() => Drop(connectionId, "getrennt");

    public void Dispose()
    {
        stop.Cancel();
        Close();
        listener?.Stop();
    }

    /// <summary>Counts the compressed bytes on the wire.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream inner;
        private readonly Action<int> count;

        public CountingStream(Stream inner, Action<int> count)
        {
            this.inner = inner;
            this.count = count;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);

        public override int Read(byte[] buffer, int offset, int length)
        {
            var read = inner.Read(buffer, offset, length);
            count(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = await inner.ReadAsync(buffer, token);
            count(read);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int length)
        {
            inner.Write(buffer, offset, length);
            count(length);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await inner.WriteAsync(buffer, token);
            count(buffer.Length);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
