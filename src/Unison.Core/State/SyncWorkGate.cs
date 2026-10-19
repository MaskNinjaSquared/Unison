// =============================================================================
// SyncWorkGate
//
// Whether background enrichment (names, pictures, group listings) should step
// aside for sync traffic. Replay comes first: after a reconnect the offline
// queue drains before anything else talks to the server, and a socket still
// awaiting its initial sync counts the same. Pictures also wait for history
// requests still out.
//
// The connection owns when a replay drain starts and ends; it only reports it
// here, so the façades can ask without reaching into the client.
// =============================================================================
using System;
using System.Diagnostics;

namespace Unison.Core.State
{
    public sealed class SyncWorkGate
    {
        private readonly HistoryOnDemandRegistry _history;
        private readonly Func<bool> _isAwaitingInitialSync;
        private volatile bool _replayDrainPending;

        public SyncWorkGate(HistoryOnDemandRegistry history, Func<bool> isAwaitingInitialSync)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _isAwaitingInitialSync = isAwaitingInitialSync ?? throw new ArgumentNullException(nameof(isAwaitingInitialSync));
        }

        public bool IsReplayDrainActive => _replayDrainPending || IsAwaitingInitialSync();

        public void SetReplayDrainPending(bool pending) => _replayDrainPending = pending;

        /// <summary>
        /// True, with the reason, when picture fetches should wait. May clear a stale full-history
        /// claim on the way, like <see cref="HistoryOnDemandRegistry.ProbeAvatarDeferral"/>.
        /// </summary>
        public bool ShouldDeferAvatarFetch(out string reason)
        {
            if (IsReplayDrainActive)
            {
                reason = "reconnect-replay-active";
                return true;
            }

            reason = _history.ProbeAvatarDeferral(out string staleCleared);
            if (staleCleared != null)
            {
                Debug.WriteLine("[SyncWorkGate] Cleared stale full-history state before avatar fetch: " + staleCleared);
            }

            return reason != null;
        }

        private bool IsAwaitingInitialSync()
        {
            try
            {
                return _isAwaitingInitialSync();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
