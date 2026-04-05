using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace COIJointVentures.Networking;

internal sealed class LocalTcpTransport : INetworkTransport
{
    private readonly ManualLogSource _log;
    private readonly int _port;
    private readonly string _host;
    private readonly object _gate = new object();
    private readonly List<ClientConnection> _clients = new List<ClientConnection>();
    private TcpListener? _listener;
    private TcpClient? _hostConnection;
    private CancellationTokenSource? _cts;
    private string? _localPeerId;
    private string? _hostPeerId;
    private bool _isConnected;

    public LocalTcpTransport(ManualLogSource log, int port, string host = "127.0.0.1")
    {
        _log = log;
        _port = port;
        _host = host;
    }

    public event Action<TransportMessage>? MessageReceived;
    public event Action<string>? ClientConnected;
    public event Action<string>? ClientDisconnected;

    public bool IsConnected => _isConnected;

    public IReadOnlyList<string> ConnectedClientIds
    {
        get
        {
            lock (_gate)
            {
                var ids = new List<string>(_clients.Count);
                foreach (var client in _clients)
                {
                    if (client.PeerId != null)
                    {
                        ids.Add(client.PeerId);
                    }
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
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _isConnected = true;
        _ = AcceptLoopAsync(_cts.Token);
        _log.LogInfo($"Local TCP transport listening on 0.0.0.0:{_port} as host '{localPeerId}'.");
    }

    public void StartClient(string localPeerId, string hostPeerId)
    {
        Dispose();

        _localPeerId = localPeerId;
        _hostPeerId = hostPeerId;
        _cts = new CancellationTokenSource();
        try
        {
            _hostConnection = new TcpClient();
            _hostConnection.Connect(_host, _port);
            _isConnected = true;
            _ = ReadLoopAsync(_hostConnection, _cts.Token, isHostConnection: true, tempId: null);
            _log.LogInfo($"Local TCP transport connected to {_host}:{_port} as client '{localPeerId}' -> host '{hostPeerId}'.");
        }
        catch
        {
            _isConnected = false;
            Dispose();
            throw;
        }
    }

    public void Broadcast(byte[] payload)
    {
        if (_hostPeerId is null)
        {
            throw new InvalidOperationException("Transport is not started.");
        }

        var message = new TransportMessage
        {
            SenderPeerId = _hostPeerId,
            Payload = payload
        };

        List<ClientConnection> snapshot;
        lock (_gate)
        {
            snapshot = new List<ClientConnection>(_clients);
        }

        foreach (var client in snapshot)
        {
            TryWrite(client.TcpClient, message, "broadcast");
        }
    }

    public void SendToClient(string peerId, byte[] payload)
    {
        if (_hostPeerId is null)
        {
            throw new InvalidOperationException("Transport is not started.");
        }

        ClientConnection? target = null;
        lock (_gate)
        {
            foreach (var client in _clients)
            {
                if (string.Equals(client.PeerId, peerId, StringComparison.Ordinal))
                {
                    target = client;
                    break;
                }
            }
        }

        if (target == null)
        {
            _log.LogWarning($"SendToClient: no connection found for peer '{peerId}'.");
            return;
        }

        var message = new TransportMessage
        {
            SenderPeerId = _hostPeerId,
            Payload = payload
        };

        TryWrite(target.TcpClient, message, $"send-to-client({peerId})");
    }

    public void SendToHost(byte[] payload)
    {
        if (_localPeerId is null || _hostConnection is null)
        {
            throw new InvalidOperationException("Client transport is not connected.");
        }

        var message = new TransportMessage
        {
            SenderPeerId = _localPeerId,
            Payload = payload
        };

        TryWrite(_hostConnection, message, "send-to-host");
    }

    public void Poll()
    {
        // tcp does its own thing with async loops, nothing to poll
    }

    public void Dispose()
    {
        _isConnected = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_listener != null)
        {
            try
            {
                _listener.Stop();
            }
            catch
            {
            }

            _listener = null;
        }

        if (_hostConnection != null)
        {
            try
            {
                _hostConnection.Close();
            }
            catch
            {
            }

            _hostConnection = null;
        }

        lock (_gate)
        {
            for (var i = 0; i < _clients.Count; i++)
            {
                try
                {
                    _clients[i].TcpClient.Close();
                }
                catch
                {
                }
            }

            _clients.Clear();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener == null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tcpClient = await _listener.AcceptTcpClientAsync();
                var tempId = tcpClient.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
                var connection = new ClientConnection(tcpClient, tempId);
                lock (_gate)
                {
                    _clients.Add(connection);
                }

                _log.LogInfo($"Accepted TCP client from {tempId}.");
                _ = ReadLoopAsync(tcpClient, cancellationToken, isHostConnection: false, tempId: tempId);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _log.LogWarning($"Local TCP accept loop failed: {ex}");
            }
        }
    }

    private async Task ReadLoopAsync(TcpClient tcpClient, CancellationToken cancellationToken, bool isHostConnection, string? tempId)
    {
        try
        {
            using var stream = tcpClient.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(stream, cancellationToken);
                if (message == null)
                {
                    break;
                }

                if (!isHostConnection && tempId != null)
                {
                    RegisterPeerId(tempId, message.SenderPeerId);
                }

                MessageReceived?.Invoke(message);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _log.LogWarning($"Local TCP read loop failed: {ex}");
            }
        }
        finally
        {
            string? disconnectedPeerId = null;
            if (!isHostConnection)
            {
                lock (_gate)
                {
                    for (var i = _clients.Count - 1; i >= 0; i--)
                    {
                        if (_clients[i].TcpClient == tcpClient)
                        {
                            disconnectedPeerId = _clients[i].PeerId;
                            _clients.RemoveAt(i);
                            break;
                        }
                    }
                }

                if (disconnectedPeerId != null)
                {
                    _log.LogInfo($"Client '{disconnectedPeerId}' disconnected.");
                    ClientDisconnected?.Invoke(disconnectedPeerId);
                }
            }
            else
            {
                _isConnected = false;
                _log.LogInfo("Disconnected from host.");
                ClientDisconnected?.Invoke(_hostPeerId ?? "host");
            }

            try
            {
                tcpClient.Close();
            }
            catch
            {
            }
        }
    }

    private void RegisterPeerId(string tempId, string senderPeerId)
    {
        lock (_gate)
        {
            foreach (var client in _clients)
            {
                if (string.Equals(client.TempId, tempId, StringComparison.Ordinal) && client.PeerId == null)
                {
                    client.PeerId = senderPeerId;
                    _log.LogInfo($"Registered peer '{senderPeerId}' for connection {tempId}.");
                    ClientConnected?.Invoke(senderPeerId);
                    break;
                }
            }
        }
    }

    private void TryWrite(TcpClient tcpClient, TransportMessage message, string operation)
    {
        try
        {
            var stream = tcpClient.GetStream();
            WriteMessage(stream, message);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Local TCP {operation} failed: {ex.Message}");
        }
    }

    private const int MaxPeerIdBytes = 256;
    private const int MaxPayloadBytes = 10 * 1024 * 1024; // 10 MB

    private static async Task<TransportMessage?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var senderLengthBuffer = await ReadExactlyAsync(stream, 4, cancellationToken);
        if (senderLengthBuffer == null)
        {
            return null;
        }

        var senderLength = BitConverter.ToInt32(senderLengthBuffer, 0);
        if (senderLength <= 0 || senderLength > MaxPeerIdBytes)
        {
            return null;
        }

        var senderBuffer = await ReadExactlyAsync(stream, senderLength, cancellationToken);
        if (senderBuffer == null)
        {
            return null;
        }

        var payloadLengthBuffer = await ReadExactlyAsync(stream, 4, cancellationToken);
        if (payloadLengthBuffer == null)
        {
            return null;
        }

        var payloadLength = BitConverter.ToInt32(payloadLengthBuffer, 0);
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes)
        {
            return null;
        }

        var payloadBuffer = await ReadExactlyAsync(stream, payloadLength, cancellationToken);
        if (payloadBuffer == null)
        {
            return null;
        }

        return new TransportMessage
        {
            SenderPeerId = Encoding.UTF8.GetString(senderBuffer),
            Payload = payloadBuffer
        };
    }

    private static void WriteMessage(Stream stream, TransportMessage message)
    {
        var senderBytes = Encoding.UTF8.GetBytes(message.SenderPeerId ?? string.Empty);
        var senderLength = BitConverter.GetBytes(senderBytes.Length);
        var payloadLength = BitConverter.GetBytes(message.Payload.Length);

        stream.Write(senderLength, 0, senderLength.Length);
        stream.Write(senderBytes, 0, senderBytes.Length);
        stream.Write(payloadLength, 0, payloadLength.Length);
        stream.Write(message.Payload, 0, message.Payload.Length);
        stream.Flush();
    }

    private static async Task<byte[]?> ReadExactlyAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer, offset, length - offset, cancellationToken);
            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        return buffer;
    }

    private sealed class ClientConnection
    {
        public ClientConnection(TcpClient tcpClient, string tempId)
        {
            TcpClient = tcpClient;
            TempId = tempId;
        }

        public TcpClient TcpClient { get; }
        public string TempId { get; }
        public string? PeerId { get; set; }
    }
}
