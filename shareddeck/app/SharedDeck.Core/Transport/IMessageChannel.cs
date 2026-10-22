namespace SharedDeck.Core.Transport;

/// <summary>A bidirectional, ordered and reliable channel of text messages.</summary>
public interface IMessageChannel
{
    bool IsConnected { get; }

    /// <summary>Raised for every received message, on a background thread, in order.</summary>
    event Action<string>? MessageReceived;

    event Action? Connected;

    event Action<string>? Disconnected;

    /// <summary>Queues a message; never blocks. Messages sent while disconnected are dropped.</summary>
    void Send(string message);
}
