using System;
using System.Collections.Generic;
using Mafi.Core.Input;

namespace COIJointVentures.Runtime;

internal static class ReplicatedCommandTracker
{
    private static readonly object Gate = new object();

    // track the actual command instances we injected from the network,
    // so we skip observing exactly those objects and nothing else
    private static readonly HashSet<IInputCommand> InjectedInstances = new(ReferenceEqualityComparer.Instance);

    // fingerprint-based dedup for incoming payloads (unchanged)
    private static readonly Dictionary<string, DateTime> PendingFingerprints = new();
    private static readonly TimeSpan FingerprintCooldown = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Mark a deserialized command as network-injected. When the scheduler
    /// processes it, ObserveCommandsFromField will skip it instead of
    /// re-sending it back out.
    /// </summary>
    public static void MarkInjected(IInputCommand command)
    {
        lock (Gate)
        {
            InjectedInstances.Add(command);
        }
    }

    /// <summary>
    /// Check if this exact command object was injected from the network.
    /// Consumes the mark so it only matches once.
    /// </summary>
    public static bool ConsumeIfInjected(IInputCommand command)
    {
        lock (Gate)
        {
            return InjectedInstances.Remove(command);
        }
    }

    public static void MarkIncoming(string payloadBase64)
    {
        lock (Gate)
        {
            PruneExpired_NoLock();
            PendingFingerprints[payloadBase64] = DateTime.UtcNow + FingerprintCooldown;
        }
    }

    public static bool IsReplicatedRecently(string payloadBase64)
    {
        lock (Gate)
        {
            PruneExpired_NoLock();
            if (!PendingFingerprints.TryGetValue(payloadBase64, out var expiresAt))
                return false;

            return expiresAt > DateTime.UtcNow;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            InjectedInstances.Clear();
            PendingFingerprints.Clear();
        }
    }

    private static void PruneExpired_NoLock()
    {
        if (PendingFingerprints.Count == 0) return;

        var now = DateTime.UtcNow;
        var expired = new List<string>();
        foreach (var pair in PendingFingerprints)
        {
            if (pair.Value <= now)
                expired.Add(pair.Key);
        }

        for (var i = 0; i < expired.Count; i++)
            PendingFingerprints.Remove(expired[i]);
    }

    /// <summary>
    /// Compares by object reference, not by value equality.
    /// </summary>
    private sealed class ReferenceEqualityComparer : IEqualityComparer<IInputCommand>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public bool Equals(IInputCommand? x, IInputCommand? y) => ReferenceEquals(x, y);
        public int GetHashCode(IInputCommand obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
