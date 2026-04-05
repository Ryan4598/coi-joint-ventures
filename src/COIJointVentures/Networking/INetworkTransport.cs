using System;
using System.Collections.Generic;

namespace COIJointVentures.Networking;

internal interface INetworkTransport : IDisposable
{
    event Action<TransportMessage>? MessageReceived;
    event Action<string>? ClientConnected;
    event Action<string>? ClientDisconnected;

    bool IsConnected { get; }

    IReadOnlyList<string> ConnectedClientIds { get; }

    void StartHost(string localPeerId);

    void StartClient(string localPeerId, string hostPeerId);

    void Broadcast(byte[] payload);

    void SendToHost(byte[] payload);

    void SendToClient(string peerId, byte[] payload);

    void Poll();
}
