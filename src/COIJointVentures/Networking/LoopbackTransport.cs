using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace COIJointVentures.Networking;

internal sealed class LoopbackTransport : INetworkTransport
{
    private readonly ManualLogSource _log;
    private string? _localPeerId;

    public LoopbackTransport(ManualLogSource log)
    {
        _log = log;
    }

#pragma warning disable CS0067
    public event Action<TransportMessage>? MessageReceived;
    public event Action<string>? ClientConnected;
    public event Action<string>? ClientDisconnected;
#pragma warning restore CS0067

    public bool IsConnected => _localPeerId != null;

    public IReadOnlyList<string> ConnectedClientIds => Array.Empty<string>();

    public void StartHost(string localPeerId)
    {
        _localPeerId = localPeerId;
        _log.LogInfo($"Loopback transport started as solo host '{localPeerId}'.");
    }

    public void StartClient(string localPeerId, string hostPeerId)
    {
        _localPeerId = localPeerId;
        _log.LogWarning("Loopback transport does not support client mode.");
    }

    public void Broadcast(byte[] payload)
    {
    }

    public void SendToHost(byte[] payload)
    {
    }

    public void SendToClient(string peerId, byte[] payload)
    {
    }

    public void Poll()
    {
    }

    public void Dispose()
    {
    }
}
