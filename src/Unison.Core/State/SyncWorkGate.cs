// =============================================================================
// SyncWorkGate
//
// Whether background enrichment (names, pictures, group listings) should step
// aside for sync traffic. Replay comes first: after a reconnect the offline
// queue drains before anything else talks to the server, and a socket still
// awaiting its initial sync counts the same. FULL_HISTORY catch-up is next:
// post-reconnect preview reconcile and usync must not starve chronological
// backlog delivery. Pictures also wait for history requests still out, and for
// the first-sync list hold (WhatsApp: unlock chats, then load photos).
//
// The connection owns when a replay drain starts and ends; catch-up and the
// first-sync UI hold are probed lazily from the host. Façades ask here without
// reaching into the client.
// =============================================================================
using System;
using System.Diagnostics;
using Unison.Core.Helpers;

namespace Unison.Core.State
{
    public sealed class SyncWorkGate
    {
        private readonly HistoryOnDemandRegistry _history;
        private readonly Func<bool> _isAwaitingInitialSync;
        private readonly Func<bool> _isHistoryCatchUpActive;
        private readonly Func<bool> _isFirstSyncUiHold;
        private volatile bool _replayDrainPending;

        public SyncWorkGate(HistoryOnDemandRegistry history, Func<bool> isAwaitingInitialSync)
            : this(history, isAwaitingInitialSync, null, null)
        {
        }

        public SyncWorkGate(
            HistoryOnDemandRegistry history,
            Func<bool> isAwaitingInitialSync,
            Func<bool> isHistoryCatchUpActive)
            : this(history, isAwaitingInitialSync, isHistoryCatchUpActive, null)
        {
        }

        public SyncWorkGate(
            HistoryOnDemandRegistry history,
            Func<bool> isAwaitingInitialSync,
            Func<bool> isHistoryCatchUpActive,
            Func<bool> isFirstSyncUiHold)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _isAwaitingInitialSync = isAwaitingInitialSync ?? throw new ArgumentNullException(nameof(isAwaitingInitialSync));
            _isHistoryCatchUpActive = isHistoryCatchUpActive;
            _isFirstSyncUiHold = isFirstSyncUiHold;
        }

        public bool IsReplayDrainActive => _replayDrainPending || IsAwaitingInitialSync();

        public void SetReplayDrainPending(bool pending) => _replayDrainPending = pending;

        /// <summary>
        /// True, with the reason, when full-catalog preview reconcile or directory enrichment
        /// (usync / names / groups) should wait for replay or catch-up.
        /// </summary>
        public bool ShouldDeferHeavyLocalWork(out string reason)
        {
            reason = HistoryCatchUpLocalWorkPolicy.DeferReason(IsHistoryCatchUpActive());
            if (reason != null)
            {
                return true;
            }

            if (IsReplayDrainActive)
            {
                reason = "reconnect-replay-active";
                return true;
            }

            reason = null;
            return false;
        }

        /// <summary>
        /// True, with the reason, when picture fetches should wait. First-sync UI hold comes
        /// before catch-up/replay so chats unlock without competing CDN traffic. May clear a
        /// stale full-history claim on the way, like <see cref="HistoryOnDemandRegistry.ProbeAvatarDeferral"/>.
        /// </summary>
        public bool ShouldDeferAvatarFetch(out string reason)
        {
            if (IsFirstSyncUiHold())
            {
                reason = FirstSyncUiAvatarPolicy.DeferReason;
                return true;
            }

            if (ShouldDeferHeavyLocalWork(out reason))
            {
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

        private bool IsHistoryCatchUpActive()
        {
            if (_isHistoryCatchUpActive == null)
            {
                return false;
            }

            try
            {
                return _isHistoryCatchUpActive();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool IsFirstSyncUiHold()
        {
            if (_isFirstSyncUiHold == null)
            {
                return false;
            }

            try
            {
                return _isFirstSyncUiHold();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
