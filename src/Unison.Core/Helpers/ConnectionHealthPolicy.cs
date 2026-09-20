// =============================================================================
// ConnectionHealthPolicy
//
// When a live socket should be trusted, probed, or torn down and reconnected.
//
// The subtlety this encodes: a WhatsApp socket can keep answering pings while
// the ordered protocol queue behind it has stopped making progress. In that
// state frames still arrive and a probe still succeeds, but user messages never
// reach the application. So a stalled node queue is never probed -- probing it
// would return "healthy" and hide the failure. It goes straight to reconnect.
//
// The thresholds come in two profiles because the same question is asked in two
// situations with different costs. The background monitor asks every 25s on a
// phone, so it leans towards leaving a slightly stale connection alone. The
// on-demand check runs with the user waiting on the answer, so it is stricter
// about staleness and more patient with the probe itself.
//
// This is policy only. Performing the reconnect stays in the host, per the
// architecture rule that ConnectionHandler never owns it.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    /// <summary>What to do with a socket after looking at its health signals.</summary>
    public enum ConnectionHealthAction
    {
        /// <summary>Trust it and check again later.</summary>
        Idle,

        /// <summary>Ask the socket to prove it is alive before deciding.</summary>
        Probe,

        /// <summary>Do not bother probing; this one has to be replaced.</summary>
        Reconnect
    }

    /// <summary>
    /// Thresholds for one situation in which socket health is evaluated. The rule is the
    /// same in both; only the tolerances differ.
    /// </summary>
    public sealed class ConnectionHealthProfile
    {
        /// <summary>
        /// How recently a frame must have arrived for the connection to be trusted without
        /// probing. The verified keep-alive normally produces an inbound IQ every twenty
        /// seconds, so both limits here are worth more than one missed beat.
        /// </summary>
        public TimeSpan FreshnessLimit { get; }

        /// <summary>How long to wait for the probe to answer before giving up on it.</summary>
        public int ProbeTimeoutMs { get; }

        private ConnectionHealthProfile(TimeSpan freshnessLimit, int probeTimeoutMs)
        {
            FreshnessLimit = freshnessLimit;
            ProbeTimeoutMs = probeTimeoutMs;
        }

        /// <summary>
        /// The periodic monitor. Runs unattended on battery, so it tolerates a staler
        /// connection and keeps the probe short rather than paying for it every cycle.
        /// </summary>
        public static readonly ConnectionHealthProfile Background =
            new ConnectionHealthProfile(TimeSpan.FromSeconds(55), 9000);

        /// <summary>
        /// Checking on the way to doing something the user asked for. Stricter about
        /// staleness because acting on a dead socket is the expensive outcome here, and
        /// more patient with the probe because the alternative is a full reconnect.
        /// </summary>
        public static readonly ConnectionHealthProfile OnDemand =
            new ConnectionHealthProfile(TimeSpan.FromSeconds(45), 10000);
    }

    public static class ConnectionHealthPolicy
    {
        /// <summary>How often the background monitor wakes up to evaluate the socket.</summary>
        public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(25);

        /// <summary>
        /// How long the ordered node queue may go without progress before the socket is
        /// considered unrecoverable rather than merely quiet.
        /// </summary>
        public static readonly TimeSpan NodeProcessingStallLimit = TimeSpan.FromSeconds(75);

        /// <summary>
        /// How long the application-level message pump may sit on one stage before it is
        /// restarted. This is a separate failure from the socket's: the pump can stall
        /// while frames, decryption and IQ traffic all continue normally, which is why it
        /// is recovered on its own instead of tearing down a healthy connection.
        /// </summary>
        public static readonly TimeSpan IncomingPumpStallLimit = TimeSpan.FromSeconds(18);

        /// <summary>
        /// The ladder of delays between reconnect attempts, in order.
        /// </summary>
        private static readonly TimeSpan[] ReconnectLadder =
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30)
        };

        /// <summary>Number of rungs before the delay stops growing.</summary>
        public static int ReconnectLadderLength
        {
            get { return ReconnectLadder.Length; }
        }

        /// <summary>
        /// What to do with a socket, given whether its node queue has stalled and whether
        /// it has seen traffic recently.
        /// </summary>
        /// <remarks>
        /// A stalled queue is deliberately not probed. The probe would succeed -- the
        /// socket is still answering -- and the stall would survive the health check.
        /// </remarks>
        public static ConnectionHealthAction Evaluate(bool nodeProcessingStalled, bool connectionIsFresh)
        {
            if (nodeProcessingStalled)
            {
                return ConnectionHealthAction.Reconnect;
            }

            return connectionIsFresh
                ? ConnectionHealthAction.Idle
                : ConnectionHealthAction.Probe;
        }

        /// <summary>
        /// How long to wait before reconnect attempt number <paramref name="attempt"/>,
        /// counted from zero. Attempts past the end of the ladder all wait the longest
        /// delay rather than growing without bound or wrapping around.
        /// </summary>
        public static TimeSpan ReconnectDelay(int attempt)
        {
            if (attempt < 0)
            {
                attempt = 0;
            }

            if (attempt >= ReconnectLadder.Length)
            {
                attempt = ReconnectLadder.Length - 1;
            }

            return ReconnectLadder[attempt];
        }
    }
}
