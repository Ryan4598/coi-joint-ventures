using System;
using System.Threading;

namespace COIJointVentures.Runtime;

internal static class ReplicationScope
{
    private static readonly AsyncLocal<int> ScopeDepth = new AsyncLocal<int>();

    public static bool IsReplicationInjection => ScopeDepth.Value > 0;

    public static IDisposable Enter()
    {
        ScopeDepth.Value = ScopeDepth.Value + 1;
        return new ScopeHandle();
    }

    private sealed class ScopeHandle : IDisposable
    {
        private bool _isDisposed;

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            ScopeDepth.Value = Math.Max(0, ScopeDepth.Value - 1);
            _isDisposed = true;
        }
    }
}

