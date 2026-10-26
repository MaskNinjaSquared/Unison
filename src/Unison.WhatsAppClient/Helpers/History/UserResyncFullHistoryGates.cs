// =============================================================================
// UserResyncFullHistoryGates
//
// Manual conversation resync (wipe + FULL_HISTORY): connect timeouts, whether to
// force a fresh transport retry, settle delay, and history-wait timeout outcome.
// Orchestration / socket I/O stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Helpers
{
    public static class UserResyncFullHistoryGates
    {
        public const int FirstConnectTimeoutMs = 35000;
        public const int FreshConnectTimeoutMs = 45000;

        /// <summary>Brief pause after forced reconnect so handshake can finish before PDO.</summary>
        public static readonly TimeSpan PostReconnectSettle = TimeSpan.FromMilliseconds(750);

        /// <summary>How long the no-socket fallback waits for history after requesting it.</summary>
        public static readonly TimeSpan HistoryWaitTimeout = TimeSpan.FromMinutes(3);

        /// <summary>
        /// First FULL_HISTORY attempt on the live socket failed — retry once on a forced
        /// fresh transport (a socket that already completed pull will not re-bootstrap).
        /// </summary>
        public static bool NeedsFreshTransportRetry(bool firstRequestSucceeded)
        {
            return !firstRequestSucceeded;
        }

        /// <summary>
        /// After the reconnect retry: success if the second send worked, or the full-history
        /// slot was claimed (force-repair consume may have won the race).
        /// </summary>
        public static bool SucceededAfterRetry(bool secondRequestSucceeded, bool fullHistoryClaimed)
        {
            return secondRequestSucceeded || fullHistoryClaimed;
        }

        /// <summary>True when <see cref="Task.WhenAny"/> finished on the delay, not the history TCS.</summary>
        public static bool HistoryWaitTimedOut(Task completed, Task historyDone)
        {
            return !ReferenceEquals(completed, historyDone);
        }
    }
}
