using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using Mafi.Core.Input;

namespace COIJointVentures.Integration;

internal static class InputSchedulerBridge
{
    public static void ScheduleReplicated(InputScheduler scheduler, IInputCommand command)
    {
        var log = Plugin.LogInstance;
        var typeName = command.GetType().FullName ?? command.GetType().Name;

        // dump queue state before and after so we can debug this mess
        var beforeCounts = DumpQueueCounts(scheduler);
        log.LogInfo($"[SCHED] BEFORE ScheduleInputCmd('{typeName}'): {beforeCounts}");

        try
        {
            scheduler.ScheduleInputCmd(command);
        }
        catch (Exception ex)
        {
            log.LogError($"[SCHED] ScheduleInputCmd THREW: {ex}");
            return;
        }

        // and after
        var afterCounts = DumpQueueCounts(scheduler);
        log.LogInfo($"[SCHED] AFTER ScheduleInputCmd('{typeName}'): {afterCounts}");
    }

    public static string DumpQueueCounts(InputScheduler scheduler)
    {
        var fields = typeof(InputScheduler).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        var parts = new List<string>();
        foreach (var field in fields)
        {
            try
            {
                var val = field.GetValue(scheduler);
                if (val is ICollection col)
                {
                    parts.Add($"{field.Name}={col.Count}");
                }
                else if (val is IEnumerable enumerable && field.FieldType.Name.Contains("Lyst"))
                {
                    int count = 0;
                    foreach (var _ in enumerable) count++;
                    parts.Add($"{field.Name}={count}");
                }
            }
            catch
            {
            }
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "no-collections-found";
    }

    public static void AppendToProcessingQueue(InputScheduler scheduler, IEnumerable<IInputCommand> commands)
    {
        var lyst = GetFieldValue(scheduler, "m_commandsToProcess");
        if (lyst == null)
        {
            Plugin.LogInstance.LogWarning("[APPEND] m_commandsToProcess is null!");
            return;
        }

        var addMethod = lyst.GetType().GetMethod("Add",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(IInputCommand) }, null);
        if (addMethod == null)
        {
            Plugin.LogInstance.LogWarning($"[APPEND] No Add(IInputCommand) method on {lyst.GetType().FullName}");
            return;
        }

        foreach (var command in commands)
        {
            addMethod.Invoke(lyst, new object[] { command });
        }
    }

    public static List<IInputCommand> ReadCommands(InputScheduler scheduler, string fieldName)
    {
        var value = GetFieldValue(scheduler, fieldName) as IEnumerable;
        var result = new List<IInputCommand>();
        if (value == null)
        {
            return result;
        }

        foreach (var item in value)
        {
            var command = item as IInputCommand;
            if (command != null)
            {
                result.Add(command);
            }
        }

        return result;
    }

    public static void ReplaceCommands(InputScheduler scheduler, string fieldName, IEnumerable<IInputCommand> commands)
    {
        var lyst = GetFieldValue(scheduler, fieldName);
        if (lyst == null)
        {
            return;
        }

        var lystType = lyst.GetType();
        var clearMethod = lystType.GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var addMethod = lystType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(IInputCommand) }, null);
        if (clearMethod == null || addMethod == null)
        {
            throw new InvalidOperationException($"Failed to manipulate scheduler command list '{fieldName}'.");
        }

        clearMethod.Invoke(lyst, null);
        foreach (var command in commands)
        {
            addMethod.Invoke(lyst, new object[] { command });
        }
    }

    private static object? GetFieldValue(InputScheduler scheduler, string fieldName)
    {
        var field = typeof(InputScheduler).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(scheduler);
    }
}
