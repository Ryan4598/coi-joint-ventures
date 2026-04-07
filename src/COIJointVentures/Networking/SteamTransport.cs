using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Steamworks;
using Steamworks.Data;

namespace COIJointVentures.Networking;

internal sealed class SteamTransport : INetworkTransport, ISocketManager, IConnectionManager
{
    private const int VirtualPort = 0;
    private const int ReceiveBufferSize = 64;

    // bump defaults so save transfers don't bottleneck
    private const int SendBufferBytes = 2 * 1024 * 1024;      // 2 MB (default 512 KB)
    private const int SendRateMaxBytes = 10 * 1024 * 1024;     // 10 MB/s (default 256 KB/s)

    private readonly ManualLogSource _log;
    private readonly object _gate = new object();

    // Host state
    private SocketManager? _socketManager;
    private Lobby? _lobby;
    private readonly Dictionary<uint, PeerConnection> _peerConnections = new();

    // Client state
    private ConnectionManager? _connectionManager;

    // Common
    private string? _localPeerId;
    private string? _hostPeerId;
    private bool _isConnected;
    private readonly Dictionary<string, string> _pendingLobbyData = new();

    public SteamTransport(ManualLogSource log)
    {
        _log = log;
    }

    public event Action<TransportMessage>? MessageReceived;
    public event Action<string>? ClientConnected;
    public event Action<string>? ClientDisconnected;

    public bool IsConnected => _isConnected;

    public Lobby? CurrentLobby => _lobby;

    public string? LobbyCode => _lobby?.Id.Value.ToString();

    public IReadOnlyList<string> ConnectedClientIds
    {
        get
        {
            lock (_gate)
            {
                var ids = new List<string>();
                foreach (var entry in _peerConnections.Values)
                {
                    ids.Add(entry.PeerId);
                }

                return ids;
            }
        }
    }

    public void StartHost(string localPeerId)
    {
        Dispose();

        _localPeerId = localPeerId;
        _hostPeerId = localPeerId;

        _socketManager = SteamNetworkingSockets.CreateRelaySocket(VirtualPort, this);
        _isConnected = true;
        _log.LogInfo($"Steam relay socket created on virtual port {VirtualPort}.");

        CreateLobbyAsync();
    }

    public void StartClient(string localPeerId, string hostPeerId)
    {
        Dispose();

        _localPeerId = localPeerId;
        _hostPeerId = hostPeerId;

        if (!ulong.TryParse(hostPeerId, out var hostIdValue))
        {
            throw new InvalidOperationException($"Invalid Steam host ID: {hostPeerId}");
        }

        var hostSteamId = new SteamId { Value = hostIdValue };
        _connectionManager = SteamNetworkingSockets.ConnectRelay(hostSteamId, VirtualPort, this);
        _log.LogInfo($"Connecting to Steam relay host {hostPeerId}...");
    }

    public void Poll()
    {
        try
        {
            _socketManager?.Receive(ReceiveBufferSize, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Steam socket receive error: {ex.Message}");
        }

        try
        {
            _connectionManager?.Receive(ReceiveBufferSize, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Steam connection receive error: {ex.Message}");
        }
    }

    public void Broadcast(byte[] payload)
    {
        List<PeerConnection> snapshot;
        lock (_gate)
        {
            snapshot = new List<PeerConnection>(_peerConnections.Values);
        }

        foreach (var peer in snapshot)
        {
            SendWithRetry(peer.Connection, payload, $"broadcast to {peer.PeerId}");
        }
    }

    public void SendToClient(string peerId, byte[] payload)
    {
        PeerConnection? target = null;
        lock (_gate)
        {
            foreach (var entry in _peerConnections.Values)
            {
                if (string.Equals(entry.PeerId, peerId, StringComparison.Ordinal))
                {
                    target = entry;
                    break;
                }
            }
        }

        if (target == null)
        {
            _log.LogWarning($"Steam SendToClient: no connection for peer '{peerId}'.");
            return;
        }

        SendWithRetry(target.Connection, payload, $"send-to-client({peerId})");
    }

    public void SendToHost(byte[] payload)
    {
        if (_connectionManager == null)
        {
            throw new InvalidOperationException("Not connected to host.");
        }

        SendWithRetry(_connectionManager.Connection, payload, "send-to-host");
    }

    /// <summary>
    /// Returns false if the send buffer is full (LimitExceeded) so callers
    /// can back off and retry next tick instead of blocking the game thread.
    /// </summary>
    public bool TrySend(Connection connection, byte[] payload, string operation)
    {
        var result = connection.SendMessage(payload, SendType.Reliable | SendType.NoNagle, 0);
        if (result == Result.OK)
            return true;

        if (result == Result.LimitExceeded)
            return false;

        _log.LogWarning($"Steam {operation} failed: {result}");
        return false;
    }

    private void SendWithRetry(Connection connection, byte[] payload, string operation)
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            var result = connection.SendMessage(payload, SendType.Reliable | SendType.NoNagle, 0);
            if (result == Result.OK)
                return;

            if (result == Result.LimitExceeded && attempt < 14)
            {
                System.Threading.Thread.Sleep(20);
                continue;
            }

            _log.LogWarning($"Steam {operation} failed: {result} (attempt {attempt + 1})");
            return;
        }
    }

    private void ConfigureConnection(Connection connection)
    {
        // Facepunch marks NetConfig as internal so we use reflection
        try
        {
            var connType = typeof(Connection);
            var setMethod = connType.GetMethod("SetConfigInt",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (setMethod == null)
            {
                _log.LogWarning("Connection.SetConfigInt not found - can't tune send params");
                return;
            }

            // NetConfig enum values: SendBufferSize=9, SendRateMin=10, SendRateMax=11
            var netConfigType = connType.Assembly.GetType("Steamworks.Data.NetConfig");
            if (netConfigType == null)
            {
                _log.LogWarning("NetConfig type not found - can't tune send params");
                return;
            }

            var boxedConn = (object)connection;
            setMethod.Invoke(boxedConn, new object[] { Enum.ToObject(netConfigType, 9), SendBufferBytes });
            setMethod.Invoke(boxedConn, new object[] { Enum.ToObject(netConfigType, 10), SendRateMaxBytes });
            setMethod.Invoke(boxedConn, new object[] { Enum.ToObject(netConfigType, 11), SendRateMaxBytes });

            _log.LogInfo($"Configured connection {connection.Id}: buffer={SendBufferBytes / 1024}KB, rate={SendRateMaxBytes / 1024}KB/s");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Failed to configure connection: {ex.Message}");
        }
    }

    public Connection? GetConnectionForPeer(string peerId)
    {
        lock (_gate)
        {
            foreach (var entry in _peerConnections.Values)
            {
                if (string.Equals(entry.PeerId, peerId, StringComparison.Ordinal))
                    return entry.Connection;
            }
        }

        return null;
    }

    public void SetLobbyData(string key, string value)
    {
        if (_lobby.HasValue)
        {
            _lobby.Value.SetData(key, value);
        }
        else
        {
            _pendingLobbyData[key] = value;
        }
    }

    public void SetJoinedLobby(Lobby lobby)
    {
        _lobby = lobby;
    }

    public void Dispose()
    {
        _isConnected = false;

        if (_socketManager != null)
        {
            try
            {
                _socketManager.Close();
            }
            catch
            {
            }

            _socketManager = null;
        }

        if (_connectionManager != null)
        {
            try
            {
                _connectionManager.Close();
            }
            catch
            {
            }

            _connectionManager = null;
        }

        if (_lobby.HasValue)
        {
            try
            {
                _lobby.Value.Leave();
            }
            catch
            {
            }

            _lobby = null;
        }

        lock (_gate)
        {
            _peerConnections.Clear();
        }
    }

    private async void CreateLobbyAsync()
    {
        try
        {
            var lobby = await SteamMatchmaking.CreateLobbyAsync(8);
            if (!lobby.HasValue)
            {
                _log.LogWarning("Failed to create Steam lobby.");
                return;
            }

            _lobby = lobby.Value;
            _lobby.Value.SetPublic();
            _lobby.Value.SetJoinable(true);
            _lobby.Value.SetData("mod", "coi-joint-ventures");
            _lobby.Value.SetData("host_id", _localPeerId ?? string.Empty);

            // flush any lobby data we queued before the lobby existed
            foreach (var kv in _pendingLobbyData)
            {
                _lobby.Value.SetData(kv.Key, kv.Value);
            }

            _pendingLobbyData.Clear();
            _log.LogInfo($"Steam lobby created: {_lobby.Value.Id.Value}");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Steam lobby creation failed: {ex.Message}");
        }
    }

    // ---- ISocketManager (host side) ----

    void ISocketManager.OnConnecting(Connection connection, ConnectionInfo info)
    {
        connection.Accept();
        _log.LogInfo($"Steam: accepting connection from {info.Identity.SteamId.Value}");
    }

    void ISocketManager.OnConnected(Connection connection, ConnectionInfo info)
    {
        ConfigureConnection(connection);

        var peerId = info.Identity.SteamId.Value.ToString();
        lock (_gate)
        {
            _peerConnections[connection.Id] = new PeerConnection(connection, peerId);
        }

        _log.LogInfo($"Steam: client {peerId} connected.");
        ClientConnected?.Invoke(peerId);
    }

    void ISocketManager.OnDisconnected(Connection connection, ConnectionInfo info)
    {
        string? peerId = null;
        lock (_gate)
        {
            if (_peerConnections.TryGetValue(connection.Id, out var entry))
            {
                peerId = entry.PeerId;
                _peerConnections.Remove(connection.Id);
            }
        }

        if (peerId != null)
        {
            _log.LogInfo($"Steam: client {peerId} disconnected.");
            ClientDisconnected?.Invoke(peerId);
        }
    }

    void ISocketManager.OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
    {
        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, size);

        string peerId;
        lock (_gate)
        {
            peerId = _peerConnections.TryGetValue(connection.Id, out var entry)
                ? entry.PeerId
                : identity.SteamId.Value.ToString();
        }

        MessageReceived?.Invoke(new TransportMessage
        {
            SenderPeerId = peerId,
            Payload = bytes
        });
    }

    // ---- IConnectionManager (client side) ----

    void IConnectionManager.OnConnecting(ConnectionInfo info)
    {
        _log.LogInfo("Steam: connecting to host relay...");
    }

    void IConnectionManager.OnConnected(ConnectionInfo info)
    {
        _isConnected = true;
        if (_connectionManager != null)
            ConfigureConnection(_connectionManager.Connection);
        _log.LogInfo("Steam: connected to host relay.");
    }

    void IConnectionManager.OnDisconnected(ConnectionInfo info)
    {
        _isConnected = false;
        _log.LogInfo($"Steam: disconnected from host (reason: {info.EndReason}).");
        ClientDisconnected?.Invoke(_hostPeerId ?? "host");
    }

    void IConnectionManager.OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
    {
        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, size);

        MessageReceived?.Invoke(new TransportMessage
        {
            SenderPeerId = _hostPeerId ?? "host",
            Payload = bytes
        });
    }

    private sealed class PeerConnection
    {
        public PeerConnection(Connection connection, string peerId)
        {
            Connection = connection;
            PeerId = peerId;
        }

        public Connection Connection { get; }
        public string PeerId { get; }
    }
}
