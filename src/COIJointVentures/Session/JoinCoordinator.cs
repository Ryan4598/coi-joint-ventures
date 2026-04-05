using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using COIJointVentures.Integration;
using COIJointVentures.Runtime;
using Mafi.Core.Input;
using Mafi.Core.Simulation;

namespace COIJointVentures.Session;

internal enum JoinPhase
{
    Idle,
    Pausing,
    WaitingForSave,
    SendingSave,
    WaitingForClients
}

internal sealed class JoinCoordinator
{
    private readonly ManualLogSource _log;
    private readonly SaveManagerBridge _saveBridge;
    private readonly SaveFileManager _saveFileManager;
    private readonly HashSet<string> _joiningPeers = new();
    private readonly List<string> _joiningNames = new();
    private readonly List<string> _lateJoiners = new(); // peers that arrived after save was sent
    private string _syncSaveName = string.Empty;
    private DateTime _phaseStarted;
    private int _pauseFrameDelay;
    private bool _saveCompletedFlag;
    private byte[]? _lastSaveBytes; // cached so late joiners can get the same save

    public JoinCoordinator(ManualLogSource log, SaveManagerBridge saveBridge, SaveFileManager saveFileManager)
    {
        _log = log;
        _saveBridge = saveBridge;
        _saveFileManager = saveFileManager;
        _saveBridge.SaveCompleted += _ => _saveCompletedFlag = true;
    }

    public JoinPhase Phase { get; private set; }
    public bool IsBlocking => Phase != JoinPhase.Idle;
    public string JoiningPlayerNames => _joiningNames.Count > 0 ? string.Join(", ", _joiningNames) : "Unknown";

    public Action<IEnumerable<string>, byte[]>? SendSaveToClients { get; set; }
    public Action? BroadcastJoinSyncBegin { get; set; }
    public Action? BroadcastJoinSyncEnd { get; set; }
    public Action? Unpause { get; set; }
    public Func<bool>? HasPendingSends { get; set; }

    public string PhaseDescription => Phase switch
    {
        JoinPhase.Pausing => "Pausing game...",
        JoinPhase.WaitingForSave => "Saving game state...",
        JoinPhase.SendingSave => _saveQueued ? "Transferring save data..." : "Preparing save...",
        JoinPhase.WaitingForClients => "Waiting for player to load...",
        _ => ""
    };

    public void BeginJoin(string peerId, string playerName)
    {
        _joiningPeers.Add(peerId);
        _joiningNames.Add(playerName);
        _log.LogInfo($"[JoinCoord] Player '{playerName}' ({peerId}) joining. Total joining: {_joiningPeers.Count}");

        if (Phase == JoinPhase.Idle)
        {
            StartJoinFlow();
        }
        else if (Phase == JoinPhase.SendingSave || Phase == JoinPhase.WaitingForClients)
        {
            // save was already sent to earlier peers — queue this peer for a
            // separate send once we have the bytes
            _lateJoiners.Add(peerId);
            _log.LogInfo($"[JoinCoord] Late joiner '{playerName}' queued for save send.");
        }
        else
        {
            // still pausing or waiting for save — they'll be included in the
            // initial SendSaveToClients call
            _log.LogInfo($"[JoinCoord] Join already in progress ({Phase}), '{playerName}' will receive same save.");
        }
    }

    public void Tick()
    {
        if (Phase == JoinPhase.Idle)
        {
            return;
        }

        // 5 min timeout per phase — large saves can take a while to transfer
        if ((DateTime.UtcNow - _phaseStarted).TotalSeconds > 300)
        {
            _log.LogError($"[JoinCoord] Phase {Phase} timed out after 30s. Aborting join flow.");
            Cleanup();
            return;
        }

        switch (Phase)
        {
            case JoinPhase.Pausing:
                TickPausing();
                break;
            case JoinPhase.WaitingForSave:
                TickWaitingForSave();
                break;
            case JoinPhase.SendingSave:
                TickSendingSave();
                break;
        }

        // send save to anyone who joined after the initial send
        FlushLateJoiners();
    }

    public void OnClientReady(string peerId)
    {
        if (!_joiningPeers.Remove(peerId))
        {
            return;
        }

        _log.LogInfo($"[JoinCoord] Client '{peerId}' is ready. Remaining: {_joiningPeers.Count}");

        if (_joiningPeers.Count == 0 && Phase == JoinPhase.WaitingForClients)
        {
            _log.LogInfo("[JoinCoord] All joining clients ready. Unpausing.");
            Unpause?.Invoke();
            BroadcastJoinSyncEnd?.Invoke();
            Phase = JoinPhase.Idle;
            _joiningNames.Clear();
        }
    }

    public void OnClientDisconnected(string peerId)
    {
        _joiningPeers.Remove(peerId);
        if (_joiningPeers.Count == 0 && Phase == JoinPhase.WaitingForClients)
        {
            _log.LogInfo("[JoinCoord] All joining clients disconnected. Unpausing.");
            Unpause?.Invoke();
            BroadcastJoinSyncEnd?.Invoke();
            Phase = JoinPhase.Idle;
            _joiningNames.Clear();
        }
    }

    private void StartJoinFlow()
    {
        _log.LogInfo("[JoinCoord] Starting join flow — pausing game.");

        // tell everyone to show the "someone is joining" overlay
        BroadcastJoinSyncBegin?.Invoke();

        // pause so the world doesn't change while we're saving
        var scheduler = PluginRuntime.Scheduler;
        if (scheduler != null)
        {
            scheduler.ScheduleInputCmd(new SetSimPauseStateCmd(isPaused: true));
        }

        Phase = JoinPhase.Pausing;
        _phaseStarted = DateTime.UtcNow;
        _pauseFrameDelay = 3; // give it a few frames to actually pause
    }

    private void TickPausing()
    {
        _pauseFrameDelay--;
        if (_pauseFrameDelay > 0)
        {
            return;
        }

        // grab save manager if we haven't yet
        var scheduler = PluginRuntime.Scheduler;
        if (scheduler != null)
        {
            _saveBridge.TryResolve(scheduler);
        }

        if (!_saveBridge.IsResolved)
        {
            _log.LogWarning("[JoinCoord] Cannot resolve ISaveManager — falling back to latest save on disk.");
            Phase = JoinPhase.SendingSave;
            _phaseStarted = DateTime.UtcNow;
            return;
        }

        _syncSaveName = $"mp_sync_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        _saveCompletedFlag = false;
        _saveBridge.RequestSave(_syncSaveName);
        Phase = JoinPhase.WaitingForSave;
        _phaseStarted = DateTime.UtcNow;
        _log.LogInfo($"[JoinCoord] Requested save '{_syncSaveName}'. Waiting for completion...");
    }

    private void TickWaitingForSave()
    {
        // gotta wait for the actual OnSaveDone event, the pending flags lie
        if (!_saveCompletedFlag)
        {
            return;
        }

        _log.LogInfo("[JoinCoord] Save completed (OnSaveDone fired). Moving to send phase.");
        Phase = JoinPhase.SendingSave;
        _sendDelayFrames = 0;
        _phaseStarted = DateTime.UtcNow;
    }

    private int _sendDelayFrames;
    private bool _saveQueued;

    private void TickSendingSave()
    {
        // chunks are queued and being paced out — wait until all delivered
        if (_saveQueued)
        {
            if (HasPendingSends?.Invoke() == true)
                return;

            _log.LogInfo("[JoinCoord] All chunks delivered. Waiting for clients to load...");
            _saveQueued = false;
            Phase = JoinPhase.WaitingForClients;
            _phaseStarted = DateTime.UtcNow;
            return;
        }

        // wait a few frames for the file to actually finish writing to disk
        if (_sendDelayFrames < 5)
        {
            _sendDelayFrames++;
            return;
        }

        byte[]? saveBytes = null;

        // try the save we just requested
        var savePath = _saveBridge.GetLastSaveFilePath();
        if (savePath != null && File.Exists(savePath))
        {
            var fileInfo = new FileInfo(savePath);
            _log.LogInfo($"[JoinCoord] Found save at LastSaveFilePath: {savePath} (size={fileInfo.Length}, lastWrite={fileInfo.LastWriteTimeUtc:HH:mm:ss.fff})");
            saveBytes = _saveFileManager.ReadSaveFile(savePath);
        }
        else
        {
            _log.LogInfo($"[JoinCoord] LastSaveFilePath returned: '{savePath}' (exists={savePath != null && File.Exists(savePath)})");
        }

        // fallback: just grab whatever's newest
        if (saveBytes == null)
        {
            var recent = _saveFileManager.FindMostRecentSave();
            if (recent != null)
            {
                saveBytes = _saveFileManager.ReadSaveFile(recent);
                _log.LogInfo($"[JoinCoord] Fallback to most recent save: {recent} ({saveBytes?.Length ?? 0} bytes)");
            }
        }

        if (saveBytes == null || saveBytes.Length == 0)
        {
            _log.LogError("[JoinCoord] No save file available. Aborting join flow.");
            Cleanup();
            return;
        }

        // hash for sanity checking transfers
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            var hash = BitConverter.ToString(md5.ComputeHash(saveBytes)).Replace("-", "");
            _log.LogInfo($"[JoinCoord] Save hash (MD5): {hash}, size: {saveBytes.Length}");
        }

        // cache for late joiners
        _lastSaveBytes = saveBytes;

        // queue the chunks — they'll be paced by TickPendingSends
        SendSaveToClients?.Invoke(_joiningPeers, saveBytes);
        _log.LogInfo($"[JoinCoord] Chunks queued for {_joiningPeers.Count} joining client(s). Waiting for delivery...");
        _saveQueued = true;
    }

    private void FlushLateJoiners()
    {
        if (_lateJoiners.Count == 0 || _lastSaveBytes == null) return;

        var batch = new List<string>(_lateJoiners);
        _lateJoiners.Clear();

        _log.LogInfo($"[JoinCoord] Sending save to {batch.Count} late joiner(s).");
        SendSaveToClients?.Invoke(batch, _lastSaveBytes);

        // if we already moved to WaitingForClients, go back to SendingSave
        // so we wait for the paced sends to finish
        if (Phase == JoinPhase.WaitingForClients)
        {
            Phase = JoinPhase.SendingSave;
            _saveQueued = true;
            _phaseStarted = DateTime.UtcNow;
        }
    }

    private void Cleanup()
    {
        Unpause?.Invoke();
        BroadcastJoinSyncEnd?.Invoke();
        Phase = JoinPhase.Idle;
        _joiningPeers.Clear();
        _joiningNames.Clear();
        _lateJoiners.Clear();
        _lastSaveBytes = null;
    }
}
