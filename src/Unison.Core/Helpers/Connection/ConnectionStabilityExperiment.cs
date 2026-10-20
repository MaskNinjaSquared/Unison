// =============================================================================
// ConnectionStabilityExperiment
//
// Single switch for the post-1.8.3 Mobile stability bundle: StreamSocket-first
// on Mobile, defer-aware health during usync/catch-up, wire-frame freshness,
// KeepAlive that tolerates pending IQs, and text-send settlement that survives
// reconnect churn. Flip <see cref="Enabled"/> to compare against the previous
// HEAD defaults without ripping the call sites out again.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Experimental connection-stability policy. Host and send helpers read this;
    /// <c>SocketBridge</c> copies the socket knobs onto <c>SocketConfig</c> at connect.
    /// </summary>
    public static class ConnectionStabilityExperiment
    {
        /// <summary>
        /// Master switch. <c>true</c> turns on the 1.8.3-like Mobile transport order plus
        /// the defer/KeepAlive/send mitigations needed under today's sync load.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Mobile opens StreamSocket first (tag 1.8.3), then MessageWebSocket.</summary>
        public static bool PreferStreamSocketOnMobile => Enabled;

        /// <summary>Health monitor defers force-reconnect while usync / FULL_HISTORY run.</summary>
        public static bool UseDeferAwareHealthMonitor => Enabled;

        /// <summary>
        /// <c>HasFreshConnection</c> uses the handler's last wire frame, not only decoded nodes.
        /// </summary>
        public static bool UseWireFrameFreshness => Enabled;

        /// <summary>KeepAlive will not declare lost while IQ waiters are outstanding.</summary>
        public static bool KeepAliveDeferWhilePendingQueries => Enabled;

        /// <summary>
        /// Minimum silence before KeepAlive death when no queries are pending
        /// (rc14 interval+grace alone is ~35s — too tight under Mobile sync).
        /// </summary>
        public static TimeSpan KeepAliveSilenceFloor =>
            Enabled ? TimeSpan.FromSeconds(75) : TimeSpan.Zero;

        /// <summary>
        /// When false, a lone IQ timeout is not tagged <c>ConnectionLost</c> (408) so logs
        /// do not look like a stream drop for a slow usync.
        /// </summary>
        public static bool TreatQueryTimeoutAsConnectionLost => !Enabled;

        public static int TextSendMaxAttempts => Enabled ? 3 : 2;

        public static TimeSpan TextSendRetryDelay =>
            Enabled ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// After the transport accepts a send, promote pending — and, when experimental,
        /// also a bubble that a racing async <c>failed</c> event already marked failed.
        /// </summary>
        public static bool ShouldPromoteBubbleAfterAccept(string currentStatus)
        {
            if (string.Equals(currentStatus, ChatMessage.StatusPending, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!Enabled)
            {
                return false;
            }

            return string.Equals(currentStatus, ChatMessage.StatusFailed, StringComparison.OrdinalIgnoreCase);
        }
    }
}
