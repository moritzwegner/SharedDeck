namespace SharedDeck.Core.Transport;

/// <summary>The network connection to the other pilot (host listens, client connects).</summary>
public interface IPeerConnector : IMessageChannel, IDisposable
{
    long BytesSent { get; }

    long BytesReceived { get; }

    void Listen(int port);

    Task ConnectAsync(string host, int port, CancellationToken token);
}
