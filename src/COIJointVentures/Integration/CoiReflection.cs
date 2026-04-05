using System;
using System.Reflection;
using Mafi.Core.Input;

namespace COIJointVentures.Integration;

internal static class CoiReflection
{
    public static MethodInfo? FindInputSchedulerProcessCommandsMethod()
    {
        return typeof(InputScheduler).GetMethod("ProcessCommands", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }
}
