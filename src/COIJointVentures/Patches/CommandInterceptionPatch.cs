using System.Collections.Generic;
using System.Reflection;
using System;
using COIJointVentures.Chat;
using COIJointVentures.Integration;
using COIJointVentures.Runtime;
using HarmonyLib;
using Mafi.Core.Input;
using COIJointVentures.Session;

namespace COIJointVentures.Patches;

internal static class CommandInterceptionPatch
{
    private static readonly HashSet<string> SeenInCurrentPass = new HashSet<string>();

    private static void Observe(IInputCommand command)
    {
        var log = PluginRuntime.Log ?? Plugin.LogInstance;
        if (!NativeCommandInspector.TryInspect(command, out var commandInfo))
        {
            return;
        }

        PluginRuntime.CommandLog?.RecordObservedCommand(commandInfo.CommandType, commandInfo.Summary);

        if (ReplicationScope.IsReplicationInjection)
        {
            return;
        }

        var session = PluginRuntime.Session;
        if (session == null || !session.ShouldReplicateCommand(commandInfo))
        {
            return;
        }

        if (session.Mode == MultiplayerMode.Client &&
            session.ActiveSince.HasValue &&
            (DateTime.UtcNow - session.ActiveSince.Value).TotalSeconds < 2.0)
        {
            log.LogInfo($"[OBS] Skipping '{commandInfo.CommandType}' — grace period active.");
            return;
        }

        var codec = PluginRuntime.NativeCodec;
        if (codec == null)
        {
            log.LogWarning("[OBS] Native command codec is not initialized.");
            return;
        }

        try
        {
            var payload = codec.SerializeToBase64(command);
            PluginRuntime.ReplicationLog?.RecordSerializedCommand(commandInfo.CommandType, Convert.FromBase64String(payload).Length);
            log.LogInfo($"[OBS] Serialized '{commandInfo.CommandType}' ({Convert.FromBase64String(payload).Length} bytes) — sending to session.");

            session.SubmitNativeCommand(command, codec);

            // toss it in the activity log if it's interesting
            var actionText = ActionDescriptionBuilder.Describe(commandInfo, command);
            if (actionText != null)
            {
                if (ActionDescriptionBuilder.IsSimControl(commandInfo))
                    session.SendSimControlLog(actionText);
                else
                    session.SendActionLog(actionText);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning($"[OBS] Failed to serialize '{commandInfo.CommandType}': {ex}");
        }
    }

    private static void Prefix(InputScheduler __instance)
    {
        var log = PluginRuntime.Log ?? Plugin.LogInstance;
        PluginRuntime.UpdateScheduler(__instance);

        var session = PluginRuntime.Session;

        // auto-confirm must run BEFORE join blocking — otherwise the joining
        // client gets stuck because IsJoinSyncActive blocks the prefix return
        if (session != null && session.State == Session.ConnectionState.LoadingSave)
        {
            log.LogInfo("[PREFIX] InputScheduler active post-load — auto-confirming save loaded.");
            session.ConfirmSaveLoaded();
        }

        // nuke all commands while someone's joining, except verification cmds
        var isJoinBlocking = session?.JoinCoordinator?.IsBlocking == true;
        var isClientSync = session?.IsJoinSyncActive == true;
        if (isJoinBlocking || isClientSync)
        {
            var cmds = InputSchedulerBridge.ReadCommands(__instance, "m_commandsToProcess");
            if (cmds.Count > 0)
            {
                var verifOnly = new List<IInputCommand>();
                foreach (var c in cmds)
                {
                    if (NativeCommandInspector.TryInspect(c, out var info) && info.IsVerificationCmd)
                    {
                        verifOnly.Add(c);
                    }
                }

                if (verifOnly.Count != cmds.Count)
                {
                    InputSchedulerBridge.ReplaceCommands(__instance, "m_commandsToProcess", verifOnly);
                }
            }

            PluginRuntime.DrainReplicated();
            return;
        }

        // drain replicated buffer and mark each instance so we don't re-observe it
        var replicated = PluginRuntime.DrainReplicated();
        if (replicated.Count > 0)
        {
            log.LogInfo($"[PREFIX] Drained {replicated.Count} replicated command(s) from buffer:");
            foreach (var cmd in replicated)
            {
                var cmdType = cmd.GetType().FullName ?? cmd.GetType().Name;
                ReplicatedCommandTracker.MarkInjected(cmd);
                log.LogInfo($"[PREFIX]   - {cmdType} (marked, IsProcessed={GetIsProcessed(cmd)})");
            }
        }

        // log queue state for debugging
        var verifCmds = InputSchedulerBridge.ReadCommands(__instance, "m_verifCmds");
        var procCmds = InputSchedulerBridge.ReadCommands(__instance, "m_commandsToProcess");
        if (verifCmds.Count > 0 || procCmds.Count > 0 || replicated.Count > 0)
        {
            log.LogInfo($"[PREFIX] Queue state: m_verifCmds={verifCmds.Count}, m_commandsToProcess={procCmds.Count}, mode={session?.Mode}");
            foreach (var cmd in procCmds)
            {
                var t = cmd.GetType().FullName ?? cmd.GetType().Name;
                log.LogInfo($"[PREFIX]   m_commandsToProcess: {t} (IsProcessed={GetIsProcessed(cmd)})");
            }
        }

        // observe the existing queue
        SeenInCurrentPass.Clear();
        ObserveCommandsFromField(__instance, "m_verifCmds", suppressReplicated: false);
        ObserveCommandsFromField(__instance, "m_commandsToProcess", suppressReplicated: true);

        // now schedule the replicated commands through the normal pipeline
        if (replicated.Count > 0)
        {
            foreach (var cmd in replicated)
            {
                InputSchedulerBridge.ScheduleReplicated(__instance, cmd);
            }

            log.LogInfo($"[PREFIX] Scheduled {replicated.Count} replicated command(s) via ScheduleInputCmd.");

            var afterCounts = InputSchedulerBridge.DumpQueueCounts(__instance);
            log.LogInfo($"[PREFIX] Queue after scheduling: {afterCounts}");
        }
    }

    private static void ObserveCommandsFromField(InputScheduler scheduler, string fieldName, bool suppressReplicated)
    {
        var log = PluginRuntime.Log ?? Plugin.LogInstance;
        var commands = InputSchedulerBridge.ReadCommands(scheduler, fieldName);
        if (commands.Count == 0)
        {
            return;
        }

        var session = PluginRuntime.Session;
        var remaining = suppressReplicated ? new List<IInputCommand>(commands.Count) : null;
        foreach (var command in commands)
        {
            var key = command.GetType().FullName ?? command.GetType().Name;
            if (!SeenInCurrentPass.Add(key + ":" + command.GetHashCode()))
            {
                if (remaining != null)
                {
                    remaining.Add(command);
                }
                continue;
            }

            // this exact instance was injected from the network, let it through
            if (ReplicatedCommandTracker.ConsumeIfInjected(command))
            {
                log.LogInfo($"[FIELD:{fieldName}] PASSTHROUGH '{key}' (network-injected, not re-observing)");
                if (remaining != null)
                {
                    remaining.Add(command);
                }
                continue;
            }

            Observe(command);

            // optimistic exec: run it locally now, drop the host echo later
            if (remaining != null)
            {
                remaining.Add(command);
            }
        }

        if (remaining != null && remaining.Count != commands.Count)
        {
            InputSchedulerBridge.ReplaceCommands(scheduler, fieldName, remaining);
            log.LogInfo($"[FIELD:{fieldName}] Replaced queue: {commands.Count} -> {remaining.Count} commands.");
        }
    }

    private static bool GetIsProcessed(IInputCommand command)
    {
        try
        {
            var prop = command.GetType().GetProperty("IsProcessed",
                BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                return (bool)prop.GetValue(command);
            }
        }
        catch
        {
        }
        return false;
    }
}
