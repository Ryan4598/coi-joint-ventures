using System;
using System.Threading.Tasks;
using BepInEx.Logging;
using COIJointVentures.Commands;
using COIJointVentures.Integration;
using COIJointVentures.Networking;
using COIJointVentures.Runtime;
using COIJointVentures.Session;
using Steamworks;

namespace COIJointVentures;

internal sealed class MultiplayerBootstrap : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly JsonCommandCodec _codec = new JsonCommandCodec();
    private readonly SaveFileManager _saveManager;
    private INetworkTransport? _transport;
    private MultiplayerSession? _session;

    public MultiplayerBootstrap(ManualLogSource log)
    {
        _log = log;
        _saveManager = new SaveFileManager(log);
    }

    public MultiplayerSession Session =>
        _session ?? throw new InvalidOperationException("Bootstrap is not initialized.");

    public SaveFileManager SaveManager => _saveManager;

    public bool IsSteamAvailable
    {
        get
        {
            try
            {
                return SteamClient.IsValid;
            }
            catch
            {
                return false;
            }
        }
    }

    public string SteamPlayerName
    {
        get
        {
            try
            {
                return SteamClient.IsValid ? SteamClient.Name : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public string SteamIdString
    {
        get
        {
            try
            {
                return SteamClient.IsValid ? SteamClient.SteamId.Value.ToString() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public SteamTransport? ActiveSteamTransport => _transport as SteamTransport;

    public string? LobbyCode => ActiveSteamTransport?.LobbyCode;

    public void Initialize()
    {
        SwitchTransport(new LoopbackTransport(_log));
        _log.LogInfo("Multiplayer bootstrap initialized in idle mode.");
    }

    // ---- LAN / Direct TCP ----

    public void HostGame(ServerConfig config, string? savePath = null)
    {
        var resolvedSavePath = savePath ?? _saveManager.FindMostRecentSave();
        SwitchTransport(new LocalTcpTransport(_log, config.Port));
        Session.StartAsHost("host", config, _saveManager, resolvedSavePath);
        _log.LogInfo($"Hosting LAN game '{config.ServerName}' on port {config.Port}.");
    }

    public void JoinGame(string playerName, string hostAddress, int port, string password)
    {
        SwitchTransport(new LocalTcpTransport(_log, port, hostAddress));
        Session.StartAsClient(playerName, "host", playerName, password);
        _log.LogInfo($"Joining LAN game at {hostAddress}:{port} as '{playerName}'.");
    }

    // ---- Steam ----

    public void HostSteamGame(ServerConfig config, string? savePath = null)
    {
        if (!IsSteamAvailable)
        {
            throw new InvalidOperationException("Steam is not available.");
        }

        var resolvedSavePath = savePath ?? _saveManager.FindMostRecentSave();
        var transport = new SteamTransport(_log);
        var peerId = SteamClient.SteamId.Value.ToString();
        SwitchTransport(transport);
        Session.StartAsHost(peerId, config, _saveManager, resolvedSavePath);

        transport.SetLobbyData("name", config.ServerName);
        if (config.HasPassword)
        {
            transport.SetLobbyData("password", "1");
        }

        _log.LogInfo($"Hosting Steam game '{config.ServerName}' as {SteamPlayerName} ({peerId}).");
    }

    public async void JoinSteamLobby(ulong lobbyId, string password)
    {
        try
        {
            if (!IsSteamAvailable)
            {
                _log.LogError("Steam is not available.");
                return;
            }

            _log.LogInfo($"Joining Steam lobby {lobbyId}...");
            var lobby = await SteamMatchmaking.JoinLobbyAsync(lobbyId);
            if (!lobby.HasValue)
            {
                _log.LogError("Failed to join Steam lobby — lobby not found or full.");
                return;
            }

            var hostSteamId = lobby.Value.Owner.Id;
            _log.LogInfo($"Joined lobby. Host is {hostSteamId.Value}.");

            var transport = new SteamTransport(_log);
            transport.SetJoinedLobby(lobby.Value);
            SwitchTransport(transport);

            var localPeerId = SteamClient.SteamId.Value.ToString();
            var hostPeerId = hostSteamId.Value.ToString();
            Session.StartAsClient(localPeerId, hostPeerId, SteamPlayerName, password);
        }
        catch (Exception ex)
        {
            _log.LogError($"Failed to join Steam lobby: {ex}");
        }
    }

    // ---- Common ----

    public void PollTransport()
    {
        _transport?.Poll();
        _session?.TickJoinCoordinator();
        _session?.TickPendingSends();
    }

    public void Disconnect()
    {
        Session.Disconnect();
        SwitchTransport(new LoopbackTransport(_log));
        _log.LogInfo("Disconnected, returned to idle mode.");
    }

    private void SwitchTransport(INetworkTransport transport)
    {
        _session?.Dispose();
        _transport?.Dispose();
        ReplicatedCommandTracker.Clear();

        _transport = transport;
        _session = new MultiplayerSession(_log, _codec, _transport);
        PluginRuntime.UpdateSession(_session);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _transport?.Dispose();
    }
}
