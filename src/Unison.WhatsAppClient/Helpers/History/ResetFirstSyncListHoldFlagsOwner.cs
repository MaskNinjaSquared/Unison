// =============================================================================
// ResetFirstSyncListHoldFlagsOwner
//
// Owns IResetFirstSyncListHoldFlagsHost. Flag clears stay as ctor ports.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ResetFirstSyncListHoldFlagsOwner : IResetFirstSyncListHoldFlagsHost
    {
        private readonly Action _clearFlags;
        private readonly Action<string> _logReset;

        public ResetFirstSyncListHoldFlagsOwner(
            Action clearFlags,
            Action<string> logReset)
        {
            _clearFlags = clearFlags
                ?? throw new ArgumentNullException(nameof(clearFlags));
            _logReset = logReset
                ?? throw new ArgumentNullException(nameof(logReset));
        }

        public void Run(string reason) =>
            ResetFirstSyncListHoldFlagsDispatch.Run(this, reason);

        void IResetFirstSyncListHoldFlagsHost.ClearFirstSyncListHoldFlags() =>
            _clearFlags();

        void IResetFirstSyncListHoldFlagsHost.LogFirstSyncListHoldFlagsReset(string reason) =>
            _logReset(reason);
    }
}
