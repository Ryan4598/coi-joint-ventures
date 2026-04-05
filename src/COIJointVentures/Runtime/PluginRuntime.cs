using System;
using System.Collections.Generic;
using BepInEx.Logging;
using COIJointVentures.Chat;
using COIJointVentures.Integration;
using COIJointVentures.Logging;
using COIJointVentures.Session;
using Mafi.Core.Input;

namespace COIJointVentures.Runtime;

internal static class PluginRuntime
{
    private static readonly object ReplicatedGate = new object();
    private static readonly List<IInputCommand> PendingReplicated = new List<IInputCommand>();

    public static ManualLogSource? Log { get; private set; }

    public static MultiplayerSession? Session { get; private set; }

    public static RuntimeCommandLog? CommandLog { get; private set; }

    public static RuntimeReplicationLog? ReplicationLog { get; private set; }

    public static NativeCommandCodec? NativeCodec { get; private set; }

    public static InputScheduler? Scheduler { get; private set; }

    public static SaveFileManager? SaveManager { get; private set; }

    public static ChatLog Chat { get; } = new ChatLog();

    public static void Initialize(ManualLogSource log, MultiplayerSession session, RuntimeCommandLog commandLog, RuntimeReplicationLog replicationLog, NativeCommandCodec nativeCodec, SaveFileManager saveManager)
    {
        Log = log;
        Session = session;
        CommandLog = commandLog;
        ReplicationLog = replicationLog;
        NativeCodec = nativeCodec;
        SaveManager = saveManager;
    }

    public static void UpdateSession(MultiplayerSession session)
    {
        Session = session;
    }

    public static DateTime LastSchedulerUpdate { get; private set; }

    public static bool IsSchedulerActive =>
        Scheduler != null && (DateTime.UtcNow - LastSchedulerUpdate).TotalSeconds < 2.0;

    public static void UpdateScheduler(InputScheduler scheduler)
    {
        Scheduler = scheduler;
        LastSchedulerUpdate = DateTime.UtcNow;
    }

    public static int PendingReplicatedCount
    {
        get { lock (ReplicatedGate) { return PendingReplicated.Count; } }
    }

    // queue up a replicated cmd, it'll get injected next ProcessCommands tick
    public static void EnqueueReplicated(IInputCommand command)
    {
        lock (ReplicatedGate)
        {
            PendingReplicated.Add(command);
        }
    }

    // grab everything in the buffer and clear it
    public static List<IInputCommand> DrainReplicated()
    {
        lock (ReplicatedGate)
        {
            if (PendingReplicated.Count == 0)
            {
                return new List<IInputCommand>();
            }

            var drained = new List<IInputCommand>(PendingReplicated);
            PendingReplicated.Clear();
            return drained;
        }
    }
}
