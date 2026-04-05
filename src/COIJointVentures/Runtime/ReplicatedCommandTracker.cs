using System;
using System.Collections.Generic;

namespace COIJointVentures.Runtime;

internal static class ReplicatedCommandTracker
{
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, DateTime> PendingFingerprints = new Dictionary<string, DateTime>();
    private static readonly Dictionary<string, DateTime> RecentlyInjectedTypes = new Dictionary<string, DateTime>();
    private static readonly Dictionary<string, DateTime> RecentlyOutboundTypes = new Dictionary<string, DateTime>();
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InjectionCooldown = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OutboundCooldown = TimeSpan.FromMilliseconds(1000);

    public static void MarkIncoming(string payloadBase64)
    {
        lock (Gate)
        {
            PruneExpired_NoLock();
            PendingFingerprints[payloadBase64] = DateTime.UtcNow + Cooldown;
        }
    }

    public static bool IsReplicatedRecently(string payloadBase64)
    {
        lock (Gate)
        {
            PruneExpired_NoLock();
            if (!PendingFingerprints.TryGetValue(payloadBase64, out var expiresAt))
            {
                return false;
            }

            return expiresAt > DateTime.UtcNow;
        }
    }

    // mark as just-injected so we don't echo it back out
    public static void MarkInjectedType(string commandTypeName)
    {
        lock (Gate)
        {
            RecentlyInjectedTypes[commandTypeName] = DateTime.UtcNow + InjectionCooldown;
        }
    }

    // was this recently shoved in from the network?
    public static bool WasRecentlyInjected(string commandTypeName)
    {
        lock (Gate)
        {
            if (!RecentlyInjectedTypes.TryGetValue(commandTypeName, out var expiresAt))
            {
                return false;
            }

            if (expiresAt <= DateTime.UtcNow)
            {
                RecentlyInjectedTypes.Remove(commandTypeName);
                return false;
            }

            return true;
        }
    }

    // we sent this to the host, so when it comes back drop it
    public static void MarkOutboundType(string commandTypeName)
    {
        lock (Gate)
        {
            RecentlyOutboundTypes[commandTypeName] = DateTime.UtcNow + OutboundCooldown;
        }
    }

    // did we just send this? if so the host echo is garbage
    public static bool WasRecentlyOutbound(string commandTypeName)
    {
        lock (Gate)
        {
            if (!RecentlyOutboundTypes.TryGetValue(commandTypeName, out var expiresAt))
            {
                return false;
            }

            if (expiresAt <= DateTime.UtcNow)
            {
                RecentlyOutboundTypes.Remove(commandTypeName);
                return false;
            }

            return true;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            PendingFingerprints.Clear();
            RecentlyInjectedTypes.Clear();
            RecentlyOutboundTypes.Clear();
        }
    }

    private static void PruneExpired_NoLock()
    {
        if (PendingFingerprints.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var expired = new List<string>();
        foreach (var pair in PendingFingerprints)
        {
            if (pair.Value <= now)
            {
                expired.Add(pair.Key);
            }
        }

        for (var i = 0; i < expired.Count; i++)
        {
            PendingFingerprints.Remove(expired[i]);
        }
    }
}
