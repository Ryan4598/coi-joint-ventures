using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx.Logging;
using COIJointVentures.Chat;
using COIJointVentures.Commands;
using COIJointVentures.Integration;
using COIJointVentures.Networking;
using COIJointVentures.Networking.Protocol;
using COIJointVentures.Runtime;
using Mafi.Core.Input;

namespace COIJointVentures.Session;

internal sealed class MultiplayerSession : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly ICommandCodec _codec;
    private readonly INetworkTransport _transport;
    private readonly Queue<PendingCommand> _pendingCommands = new();
    private readonly HashSet<string> _activePeers = new();
    private readonly HashSet<string> _pendingPeers = new();
    private readonly Dictionary<string, string> _peerNames = new();
    private readonly HashSet<Guid> _outboundCommandIds = new();
    private readonly HashSet<Guid> _seenCommandIds = new();
    private long _nextSequence;

    private ServerConfig? _serverConfig;
    private SaveFileManager? _saveManager;
    private string? _currentSavePath;

    // Host-side: join coordination
    private JoinCoordinator? _joinCoordinator;
    private SaveManagerBridge? _saveBridge;

    // Client-side: join sync overlay
    public bool IsJoinSyncActive { get; private set; }
    public string JoinSyncPlayerName { get; private set; } = string.Empty;

    // Client-side: save data received from host, waiting to be written/loaded
    private byte[]? _receivedSaveData;
    private string? _receivedSavePath;

    // Client-side: chunked save reassembly
    private byte[]? _saveChunkBuffer;
    private int _saveChunksReceived;
    private int _saveChunksExpected;

    public MultiplayerSession(ManualLogSource log, ICommandCodec codec, INetworkTransport transport)
    {
        _log = log;
        _codec = codec;
        _transport = transport;
        _transport.MessageReceived += OnTransportMessageReceived;
        _transport.ClientDisconnected += OnClientDisconnected;
    }

    public MultiplayerMode Mode { get; private set; }

    public ConnectionState State { get; private set; }

    public string LocalPeerId { get; private set; } = string.Empty;

    public string HostPeerId { get; private set; } = string.Empty;

    public string StatusMessage { get; private set; } = string.Empty;

    // when we went active, used for the startup grace period
    public DateTime? ActiveSince { get; private set; }

    public JoinCoordinator? JoinCoordinator => _joinCoordinator;

    public string LocalPlayerName { get; private set; } = string.Empty;

    // set when the host drops us — the plugin checks this and boots us to menu
    public bool HostDisconnected { get; private set; }

    public IReadOnlyCollection<string> ActivePeers
    {
        get
        {
            lock (_activePeers)
            {
                return new List<string>(_activePeers);
            }
        }
    }

    public IReadOnlyCollection<string> PendingPeers
    {
        get
        {
            lock (_pendingPeers)
            {
                return new List<string>(_pendingPeers);
            }
        }
    }

    public byte[]? ReceivedSaveData => _receivedSaveData;
    public string? ReceivedSavePath => _receivedSavePath;

    public void StartAsHost(string peerId, ServerConfig config, SaveFileManager saveManager, string? currentSavePath)
    {
        _pendingCommands.Clear();
        _serverConfig = config;
        _saveManager = saveManager;
        _currentSavePath = currentSavePath;

        _saveBridge = new SaveManagerBridge(_log);
        _joinCoordinator = new JoinCoordinator(_log, _saveBridge, saveManager);
        _joinCoordinator.SendSaveToClients = SendSaveToClients;
        _joinCoordinator.BroadcastJoinSyncBegin = () =>
        {
            _transport.Broadcast(ProtocolCodec.WrapJoinSyncBegin(_joinCoordinator.JoiningPlayerNames));
        };
        _joinCoordinator.BroadcastJoinSyncEnd = () =>
        {
            _transport.Broadcast(ProtocolCodec.WrapJoinSyncEnd());
        };
        _joinCoordinator.Unpause = () =>
        {
            var scheduler = PluginRuntime.Scheduler;
            if (scheduler != null)
            {
                scheduler.ScheduleInputCmd(new Mafi.Core.Simulation.SetSimPauseStateCmd(isPaused: false));
            }
        };
        _joinCoordinator.HasPendingSends = () => HasPendingSends;

        LocalPeerId = peerId;
        HostPeerId = peerId;
        LocalPlayerName = ResolvePeerName(peerId);
        _transport.StartHost(LocalPeerId);
        Mode = MultiplayerMode.Host;
        State = ConnectionState.Hosting;
        StatusMessage = $"Hosting '{config.ServerName}'" +
                        (config.HasPassword ? " (password protected)" : "");
        _log.LogInfo(StatusMessage);
    }

    public void StartAsClient(string localPeerId, string hostPeerId, string playerName, string password)
    {
        _pendingCommands.Clear();
        LocalPeerId = localPeerId;
        HostPeerId = hostPeerId;
        LocalPlayerName = playerName;
        State = ConnectionState.Connecting;
        StatusMessage = $"Connecting to {hostPeerId}...";

        try
        {
            _transport.StartClient(LocalPeerId, HostPeerId);
        }
        catch (Exception ex)
        {
            State = ConnectionState.Idle;
            Mode = MultiplayerMode.None;
            StatusMessage = $"Connection failed: {ex.Message}";
            _log.LogWarning(StatusMessage);
            throw;
        }

        // connected, fire off join request
        State = ConnectionState.WaitingForAccept;
        StatusMessage = "Connected, sending join request...";

        var joinRequest = new JoinRequest
        {
            PlayerName = playerName,
            Password = password
        };

        _transport.SendToHost(ProtocolCodec.WrapJoinRequest(joinRequest));
        _log.LogInfo($"Sent join request as '{playerName}'.");
    }

    public void SubmitLocalCommand(string commandType, string payloadJson, Action<CommandEnvelope> apply)
    {
        var envelope = new CommandEnvelope
        {
            CommandType = commandType,
            IssuerPlayerId = LocalPeerId,
            Sequence = Interlocked.Increment(ref _nextSequence),
            Tick = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PayloadJson = payloadJson
        };

        if (Mode == MultiplayerMode.Host)
        {
            ApplyAsHost(envelope, apply);
            return;
        }

        // remember this ID so we drop the echo when host sends it back
        _outboundCommandIds.Add(envelope.CommandId);
        _pendingCommands.Enqueue(new PendingCommand(envelope));
        _transport.SendToHost(ProtocolCodec.WrapGameCommand(_codec.Encode(envelope)));
    }

    public bool ShouldReplicateCommand(NativeCommandInfo command)
    {
        if (Mode == MultiplayerMode.None)
        {
            return false;
        }

        if (command.IsVerificationCmd)
        {
            return false;
        }

        // replicate everything except verification cmds
        // (pause cmd has AffectsSaveState=false but still needs to go through)
        return true;
    }

    public void SubmitNativeCommand(IInputCommand command, NativeCommandCodec codec)
    {
        var typeName = command.GetType().FullName ?? command.GetType().Name;
        var payloadBase64 = codec.SerializeToBase64(command);
        SubmitLocalCommand(typeName, payloadBase64, _ => { });
    }

    public void ReceiveReplicatedCommand(CommandEnvelope envelope)
    {
        var codec = PluginRuntime.NativeCodec;
        if (codec == null)
        {
            _log.LogWarning($"[RECV] Cannot deserialize '{envelope.CommandType}' — codec unavailable.");
            return;
        }

        try
        {
            var nativeCommand = codec.DeserializeFromBase64(envelope.PayloadJson);
            var deserializedType = nativeCommand.GetType().FullName ?? nativeCommand.GetType().Name;
            var isProcessed = false;
            try { isProcessed = nativeCommand.IsProcessed; } catch { }
            _log.LogInfo($"[RECV] Deserialized '{envelope.CommandType}' -> actual type='{deserializedType}', IsProcessed={isProcessed}, AffectsSaveState={nativeCommand.AffectsSaveState}");
            PluginRuntime.EnqueueReplicated(nativeCommand);
            _log.LogInfo($"[RECV] Buffered '{deserializedType}' for injection. Buffer size={PluginRuntime.PendingReplicatedCount}");
        }
        catch (Exception ex)
        {
            _log.LogError($"[RECV] FAILED to deserialize '{envelope.CommandType}': {ex}");
        }
    }

    public void ClearReceivedSave()
    {
        _receivedSaveData = null;
        _receivedSavePath = null;
    }

    public void ConfirmSaveLoaded()
    {
        if (State != ConnectionState.ReceivingSave && State != ConnectionState.LoadingSave)
        {
            return;
        }

        State = ConnectionState.Connected;
        Mode = MultiplayerMode.Client;
        ActiveSince = DateTime.UtcNow;
        StatusMessage = "Connected to server, playing.";

        // clear join sync flag — we might have received JoinSyncBegin about ourselves
        IsJoinSyncActive = false;
        JoinSyncPlayerName = string.Empty;

        _transport.SendToHost(ProtocolCodec.WrapClientReady());
        _log.LogInfo("Save loaded, sent ClientReady to host. Session is now active.");
    }

    public void Disconnect()
    {
        _transport.Dispose();
        Mode = MultiplayerMode.None;
        State = ConnectionState.Idle;
        StatusMessage = "Disconnected.";
        HostDisconnected = false;
        IsJoinSyncActive = false;
        JoinSyncPlayerName = string.Empty;
        _pendingCommands.Clear();
        lock (_activePeers) { _activePeers.Clear(); }
        lock (_pendingPeers) { _pendingPeers.Clear(); }
        _receivedSaveData = null;
        _receivedSavePath = null;
        _log.LogInfo("Session disconnected.");
    }

    private void ApplyAsHost(CommandEnvelope envelope, Action<CommandEnvelope> apply)
    {
        _log.LogInfo($"[HOST-APPLY] Broadcasting '{envelope.CommandType}' to all clients.");
        apply(envelope);
        _transport.Broadcast(ProtocolCodec.WrapGameCommand(_codec.Encode(envelope)));
    }

    private void OnTransportMessageReceived(TransportMessage message)
    {
        if (message.Payload.Length == 0)
        {
            return;
        }

        var msgType = ProtocolCodec.ReadType(message.Payload);
        var payload = ProtocolCodec.ReadPayload(message.Payload);

        switch (msgType)
        {
            case ProtocolMessageType.JoinRequest:
                HandleJoinRequest(message.SenderPeerId, payload);
                break;
            case ProtocolMessageType.JoinAccepted:
                HandleJoinAccepted(payload);
                break;
            case ProtocolMessageType.JoinRejected:
                HandleJoinRejected(payload);
                break;
            case ProtocolMessageType.SaveData:
                HandleSaveData(payload);
                break;
            case ProtocolMessageType.SaveChunk:
                HandleSaveChunk(payload);
                break;
            case ProtocolMessageType.SaveComplete:
                HandleSaveComplete();
                break;
            case ProtocolMessageType.ClientReady:
                HandleClientReady(message.SenderPeerId);
                break;
            case ProtocolMessageType.JoinSyncBegin:
                IsJoinSyncActive = true;
                JoinSyncPlayerName = ProtocolCodec.DecodeJoinSyncBegin(payload);
                _log.LogInfo($"[JOIN-SYNC] Join sync active: '{JoinSyncPlayerName}' is joining. Pausing sim.");
                // pause while they're joining
                var pauseScheduler = Runtime.PluginRuntime.Scheduler;
                if (pauseScheduler != null)
                {
                    pauseScheduler.ScheduleInputCmd(new Mafi.Core.Simulation.SetSimPauseStateCmd(isPaused: true));
                }
                break;
            case ProtocolMessageType.JoinSyncEnd:
                IsJoinSyncActive = false;
                JoinSyncPlayerName = string.Empty;
                _log.LogInfo("[JOIN-SYNC] Join sync ended. Unpausing sim.");
                // unpause, they're in
                var unpauseScheduler = Runtime.PluginRuntime.Scheduler;
                if (unpauseScheduler != null)
                {
                    unpauseScheduler.ScheduleInputCmd(new Mafi.Core.Simulation.SetSimPauseStateCmd(isPaused: false));
                }
                break;
            case ProtocolMessageType.ChatMessage:
                HandleChatMessage(message.SenderPeerId, payload);
                break;
            case ProtocolMessageType.GameCommand:
                HandleGameCommand(message.SenderPeerId, payload);
                break;
            default:
                _log.LogWarning($"Unknown protocol message type: {(byte)msgType} from '{message.SenderPeerId}'.");
                break;
        }
    }

    private void HandleJoinRequest(string senderPeerId, byte[] payload)
    {
        if (Mode != MultiplayerMode.Host)
        {
            _log.LogWarning($"Received JoinRequest from '{senderPeerId}' but not in host mode.");
            return;
        }

        var request = ProtocolCodec.DecodeJoinRequest(payload);
        _log.LogInfo($"Received join request from '{request.PlayerName}'.");

        // check password
        if (_serverConfig != null && _serverConfig.HasPassword)
        {
            if (!string.Equals(_serverConfig.Password, request.Password, StringComparison.Ordinal))
            {
                _log.LogInfo($"Rejected '{request.PlayerName}': wrong password.");
                var rejection = new JoinResponse
                {
                    Accepted = false,
                    Reason = "Incorrect password."
                };
                _transport.SendToClient(senderPeerId, ProtocolCodec.WrapJoinRejected(rejection));
                return;
            }
        }

        // looks good, let em in
        lock (_peerNames) { _peerNames[senderPeerId] = request.PlayerName; }
        lock (_pendingPeers) { _pendingPeers.Add(senderPeerId); }

        var acceptance = new JoinResponse
        {
            Accepted = true,
            ServerName = _serverConfig?.ServerName ?? "COI Server",
            AssignedPeerId = senderPeerId
        };
        _transport.SendToClient(senderPeerId, ProtocolCodec.WrapJoinAccepted(acceptance));
        _log.LogInfo($"Accepted '{request.PlayerName}' (peer={senderPeerId}). Starting coordinated join...");

        // kick off the whole pause->save->send->wait flow
        if (_joinCoordinator != null)
        {
            _joinCoordinator.BeginJoin(senderPeerId, request.PlayerName);
        }
        else
        {
            // fallback if coordinator isn't set up
            SendSaveToClient(senderPeerId);
        }
    }

    private void SendSaveToClient(string peerId)
    {
        if (_saveManager == null)
        {
            _log.LogWarning("Save manager not available, cannot send save to client.");
            return;
        }

        var savePath = _currentSavePath ?? _saveManager.FindMostRecentSave();
        if (savePath == null)
        {
            _log.LogWarning("No save file found to send to client.");
            _transport.SendToClient(peerId, ProtocolCodec.WrapSaveData(Array.Empty<byte>()));
            return;
        }

        var saveBytes = _saveManager.ReadSaveFile(savePath);
        if (saveBytes == null || saveBytes.Length == 0)
        {
            _log.LogWarning($"Failed to read save file: {savePath}");
            _transport.SendToClient(peerId, ProtocolCodec.WrapSaveData(Array.Empty<byte>()));
            return;
        }

        // chunk it up so steam doesn't choke
        var chunkSize = ProtocolCodec.MaxChunkSize;
        var totalChunks = (saveBytes.Length + chunkSize - 1) / chunkSize;
        _log.LogInfo($"Sending save to '{peerId}': {saveBytes.Length} bytes in {totalChunks} chunk(s) from {savePath}");

        for (int i = 0; i < totalChunks; i++)
        {
            var offset = i * chunkSize;
            var length = Math.Min(chunkSize, saveBytes.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(saveBytes, offset, chunk, 0, length);
            _transport.SendToClient(peerId, ProtocolCodec.WrapSaveChunk(i, totalChunks, saveBytes.Length, chunk));
        }

        _transport.SendToClient(peerId, ProtocolCodec.WrapSaveComplete());
        _log.LogInfo($"Save transfer complete to '{peerId}'.");
    }

    // paced sender so we don't slam steam's rate limits
    private readonly Queue<Action> _pendingSends = new();
    private int _sendPaceCounter;

    public bool HasPendingSends => _pendingSends.Count > 0;

    public void TickPendingSends()
    {
        if (_pendingSends.Count == 0)
        {
            return;
        }

        // 1 message per 10 ticks, roughly 166ms at 60fps
        _sendPaceCounter++;
        if (_sendPaceCounter < 10)
        {
            return;
        }

        _sendPaceCounter = 0;
        _pendingSends.Dequeue()();
    }

    private void SendSaveToClients(IEnumerable<string> peerIds, byte[] saveBytes)
    {
        var chunkSize = ProtocolCodec.MaxChunkSize;
        var totalChunks = (saveBytes.Length + chunkSize - 1) / chunkSize;
        var peerList = new List<string>(peerIds);

        foreach (var peerId in peerList)
        {
            _log.LogInfo($"Queuing save for '{peerId}': {saveBytes.Length} bytes in {totalChunks} chunk(s)");

            for (int i = 0; i < totalChunks; i++)
            {
                var offset = i * chunkSize;
                var length = Math.Min(chunkSize, saveBytes.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(saveBytes, offset, chunk, 0, length);
                var chunkMsg = ProtocolCodec.WrapSaveChunk(i, totalChunks, saveBytes.Length, chunk);
                var targetPeer = peerId;
                var chunkIndex = i;
                _pendingSends.Enqueue(() =>
                {
                    _log.LogInfo($"[CHUNK-SEND] Sending chunk {chunkIndex + 1}/{totalChunks} to '{targetPeer}'");
                    _transport.SendToClient(targetPeer, chunkMsg);
                });
            }

            var completePeer = peerId;
            _pendingSends.Enqueue(() =>
            {
                _log.LogInfo($"[CHUNK-SEND] Sending SaveComplete to '{completePeer}'");
                _transport.SendToClient(completePeer, ProtocolCodec.WrapSaveComplete());
            });
        }

        _log.LogInfo($"Queued {_pendingSends.Count} send operations for {peerList.Count} client(s).");
    }

    private void HandleJoinAccepted(byte[] payload)
    {
        if (State != ConnectionState.WaitingForAccept)
        {
            return;
        }

        var response = ProtocolCodec.DecodeJoinResponse(payload);
        State = ConnectionState.ReceivingSave;
        StatusMessage = $"Accepted by '{response.ServerName}', receiving save...";
        _log.LogInfo(StatusMessage);
    }

    private void HandleJoinRejected(byte[] payload)
    {
        var response = ProtocolCodec.DecodeJoinResponse(payload);
        State = ConnectionState.Idle;
        Mode = MultiplayerMode.None;
        StatusMessage = $"Rejected: {response.Reason}";
        _log.LogInfo($"Join rejected: {response.Reason}");
    }

    private void HandleSaveData(byte[] saveBytes)
    {
        if (State != ConnectionState.ReceivingSave)
        {
            _log.LogWarning($"Received save data ({saveBytes.Length} bytes) but state is {State}, ignoring.");
            return;
        }

        // hash check so we know the save isn't corrupted
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            var hash = BitConverter.ToString(md5.ComputeHash(saveBytes)).Replace("-", "");
            _log.LogInfo($"[SAVE-RECV] Save hash (MD5): {hash}, size: {saveBytes.Length}");
        }

        if (saveBytes.Length == 0)
        {
            _log.LogWarning("Received empty save data from host. The host may not have a save file.");
            StatusMessage = "Warning: host sent empty save. You may need to load a save manually.";
            ConfirmSaveLoaded();
            return;
        }

        _log.LogInfo($"Received save data: {saveBytes.Length} bytes. Writing to disk...");
        _receivedSaveData = saveBytes;
        State = ConnectionState.LoadingSave;
        StatusMessage = $"Received save ({saveBytes.Length / 1024} KB). Loading...";

        var gameName = "Multiplayer";
        var saveName = $"mp_joined_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

        // write it to disk
        var saveManager = PluginRuntime.SaveManager;
        string? writtenPath = saveManager?.WriteSaveFile(saveBytes, saveName, gameName);

        _receivedSavePath = writtenPath;

        if (writtenPath == null)
        {
            StatusMessage = "Failed to write save file.";
            _log.LogError("Could not write received save to disk.");
            return;
        }

        // try auto-loading, fingers crossed
        if (MainCapture.HasMain)
        {
            StatusMessage = "Loading save...";
            _log.LogInfo($"Auto-loading save '{saveName}' (game: '{gameName}') via IMain.LoadGame...");
            if (MainCapture.TryLoadGame(saveName, gameName))
            {
                StatusMessage = "Loading game...";
                return;
            }

            _log.LogWarning("Auto-load failed, falling back to manual load.");
        }

        // auto-load failed, they'll have to do it manually
        StatusMessage = $"Save written. Load '{saveName}' from the game's load menu, then click Confirm.";
        _log.LogInfo($"Manual load required: {writtenPath}");
    }

    // 512 MB — no legit COI save comes close to this
    private const int MaxSaveSize = 512 * 1024 * 1024;

    private void HandleSaveChunk(byte[] payload)
    {
        ProtocolCodec.DecodeSaveChunk(payload, out var chunkIndex, out var totalChunks, out var totalSize, out var chunkData);

        if (chunkIndex == 0)
        {
            if (totalSize <= 0 || totalSize > MaxSaveSize || totalChunks <= 0)
            {
                _log.LogWarning($"Rejecting save: totalSize={totalSize}, totalChunks={totalChunks} — out of range.");
                return;
            }

            _saveChunkBuffer = new byte[totalSize];
            _saveChunksReceived = 0;
            _saveChunksExpected = totalChunks;
            State = ConnectionState.ReceivingSave;
            StatusMessage = $"Receiving save ({totalSize / 1024} KB)...";
            _log.LogInfo($"Starting chunked save receive: {totalSize} bytes in {totalChunks} chunks.");
        }

        if (_saveChunkBuffer == null)
        {
            _log.LogWarning("Received save chunk but no buffer initialized.");
            return;
        }

        if (chunkIndex < 0 || chunkIndex >= _saveChunksExpected)
        {
            _log.LogWarning($"Rejecting chunk with out-of-range index {chunkIndex} (expected 0..{_saveChunksExpected - 1}).");
            return;
        }

        long offset = (long)chunkIndex * ProtocolCodec.MaxChunkSize;
        if (offset + chunkData.Length > _saveChunkBuffer.Length)
        {
            _log.LogWarning($"Rejecting chunk {chunkIndex}: write at {offset}+{chunkData.Length} exceeds buffer size {_saveChunkBuffer.Length}.");
            return;
        }

        Buffer.BlockCopy(chunkData, 0, _saveChunkBuffer, (int)offset, chunkData.Length);
        _saveChunksReceived++;
        StatusMessage = $"Receiving save... ({_saveChunksReceived}/{_saveChunksExpected})";
        _log.LogDebug($"Received save chunk {chunkIndex + 1}/{totalChunks} ({chunkData.Length} bytes).");
    }

    private void HandleSaveComplete()
    {
        if (_saveChunkBuffer == null || _saveChunksReceived < _saveChunksExpected)
        {
            _log.LogWarning($"Save complete signal but only {_saveChunksReceived}/{_saveChunksExpected} chunks received.");
        }

        _log.LogInfo($"All save chunks received ({_saveChunkBuffer?.Length ?? 0} bytes). Processing...");
        var saveBytes = _saveChunkBuffer ?? Array.Empty<byte>();
        _saveChunkBuffer = null;
        _saveChunksReceived = 0;
        _saveChunksExpected = 0;

        // hand it off to the normal save handler
        HandleSaveData(saveBytes);
    }

    private void HandleClientReady(string senderPeerId)
    {
        if (Mode != MultiplayerMode.Host)
        {
            return;
        }

        lock (_pendingPeers) { _pendingPeers.Remove(senderPeerId); }
        lock (_activePeers) { _activePeers.Add(senderPeerId); }

        _log.LogInfo($"Client '{senderPeerId}' is ready and active.");
        StatusMessage = $"Hosting — {_activePeers.Count} player(s) connected.";

        PluginRuntime.Chat.AddSystem($"{ResolvePeerName(senderPeerId)} joined the game.");
        _joinCoordinator?.OnClientReady(senderPeerId);
    }

    private void HandleChatMessage(string senderPeerId, byte[] payload)
    {
        var msg = ProtocolCodec.DecodeChatMessage(payload);

        // host stamps the real sender identity before relaying — don't trust the payload
        if (Mode == MultiplayerMode.Host && senderPeerId != HostPeerId)
        {
            msg.SenderPeerId = senderPeerId;
            msg.SenderName = ResolvePeerName(senderPeerId);
            _transport.Broadcast(ProtocolCodec.WrapChatMessage(msg));
        }

        // skip our own messages, already in the log
        if (string.Equals(msg.SenderPeerId, LocalPeerId, StringComparison.Ordinal))
        {
            return;
        }

        if (msg.Kind == 0)
        {
            PluginRuntime.Chat.AddChat(msg.SenderName, msg.Text);
        }
        else
        {
            PluginRuntime.Chat.AddAction(msg.SenderName, msg.Text);
        }
    }

    public void SendChatMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var msg = new ChatMessagePayload
        {
            SenderName = LocalPlayerName,
            SenderPeerId = LocalPeerId,
            Kind = 0,
            Text = text
        };

        var wrapped = ProtocolCodec.WrapChatMessage(msg);
        if (Mode == MultiplayerMode.Host)
        {
            _transport.Broadcast(wrapped);
        }
        else
        {
            _transport.SendToHost(wrapped);
        }

        PluginRuntime.Chat.AddChat(LocalPlayerName, text);
    }

    public void SendActionLog(string description)
    {
        var msg = new ChatMessagePayload
        {
            SenderName = LocalPlayerName,
            SenderPeerId = LocalPeerId,
            Kind = 1,
            Text = description
        };

        var wrapped = ProtocolCodec.WrapChatMessage(msg);
        if (Mode == MultiplayerMode.Host)
        {
            _transport.Broadcast(wrapped);
        }
        else
        {
            _transport.SendToHost(wrapped);
        }

        PluginRuntime.Chat.AddAction(LocalPlayerName, description);
    }

    private string ResolvePeerName(string peerId)
    {
        lock (_peerNames)
        {
            if (_peerNames.TryGetValue(peerId, out var name))
            {
                return name;
            }
        }

        try
        {
            if (ulong.TryParse(peerId, out var steamIdValue))
            {
                var friend = new Steamworks.Friend(new Steamworks.SteamId { Value = steamIdValue });
                if (!string.IsNullOrEmpty(friend.Name))
                {
                    return friend.Name;
                }
            }
        }
        catch
        {
        }

        return peerId;
    }

    private void HandleGameCommand(string senderPeerId, byte[] payload)
    {
        _log.LogInfo($"[GAME-CMD] Received from '{senderPeerId}', payload={payload.Length} bytes, Mode={Mode}, HostPeerId={HostPeerId}");

        var envelope = _codec.Decode(payload);
        if (!envelope.IsValid)
        {
            _log.LogWarning($"[GAME-CMD] Rejected invalid command from '{senderPeerId}'.");
            return;
        }

        _log.LogInfo($"[GAME-CMD] Decoded: type='{envelope.CommandType}', issuer='{envelope.IssuerPlayerId}', seq={envelope.Sequence}");

        if (!_seenCommandIds.Add(envelope.CommandId))
        {
            _log.LogWarning($"[GAME-CMD] Duplicate CommandId {envelope.CommandId} from '{senderPeerId}' — dropped.");
            return;
        }

        if (Mode == MultiplayerMode.Host && senderPeerId != HostPeerId)
        {
            if (!string.Equals(envelope.IssuerPlayerId, senderPeerId, StringComparison.Ordinal))
            {
                _log.LogWarning($"[GAME-CMD] Rejected command from '{senderPeerId}': IssuerPlayerId '{envelope.IssuerPlayerId}' doesn't match sender.");
                return;
            }

            _log.LogInfo($"[GAME-CMD] HOST: Processing client command '{envelope.CommandType}' from '{senderPeerId}' -> reinject + broadcast.");
            ReceiveReplicatedCommand(envelope);
            _transport.Broadcast(ProtocolCodec.WrapGameCommand(payload));
            return;
        }

        if (Mode == MultiplayerMode.Client && senderPeerId == HostPeerId)
        {
            // drop echoes of our own commands, already ran em
            if (_outboundCommandIds.Remove(envelope.CommandId))
            {
                _log.LogInfo($"[GAME-CMD] CLIENT: Dropped echo of own command '{envelope.CommandType}' id={envelope.CommandId} (already executed locally).");
                if (_pendingCommands.Count > 0 && _pendingCommands.Peek().Envelope.CommandId == envelope.CommandId)
                {
                    _pendingCommands.Dequeue();
                }
                return;
            }

            _log.LogInfo($"[GAME-CMD] CLIENT: Received host command '{envelope.CommandType}' seq={envelope.Sequence} -> reinject.");
            ReceiveReplicatedCommand(envelope);
            return;
        }

        _log.LogWarning($"[GAME-CMD] UNHANDLED: sender='{senderPeerId}', mode={Mode}, hostPeer='{HostPeerId}' — command dropped!");
    }

    // fired when someone drops — could be a client (if we're host) or the host (if we're client)
    private void OnClientDisconnected(string peerId)
    {
        bool wasActive;
        lock (_activePeers) { wasActive = _activePeers.Remove(peerId); }
        lock (_pendingPeers) { _pendingPeers.Remove(peerId); }

        if (wasActive && Mode == MultiplayerMode.Host)
        {
            _log.LogInfo($"Active client '{peerId}' disconnected.");
            StatusMessage = $"Hosting — {_activePeers.Count} player(s) connected.";
            PluginRuntime.Chat.AddSystem($"{ResolvePeerName(peerId)} left the game.");
        }

        // if we're a client and the host dropped, we're fucked — flag it
        if (Mode == MultiplayerMode.Client && string.Equals(peerId, HostPeerId, StringComparison.Ordinal))
        {
            _log.LogInfo("Host disconnected. Session is dead.");
            StatusMessage = "Host disconnected.";
            HostDisconnected = true;
        }

        _joinCoordinator?.OnClientDisconnected(peerId);
    }

    public void TickJoinCoordinator()
    {
        _joinCoordinator?.Tick();
    }

    public void Dispose()
    {
        _transport.MessageReceived -= OnTransportMessageReceived;
        _transport.ClientDisconnected -= OnClientDisconnected;
    }
}
