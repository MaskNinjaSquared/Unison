// =============================================================================
// ConnectionSessionFlags
//
// Reconnect / pairing / logout latches for the live connection. One instance per
// app (DI singleton); the host and its wire owners read and write the same flags.
// =============================================================================
using System.Threading;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ConnectionSessionFlags
    {
        private volatile bool _suppressReconnect;
        private volatile bool _fatalSessionEnded;
        private volatile bool _sessionEstablishedThisConnection;
        private volatile bool _pairingRestartPending;
        private volatile bool _countPreSessionCloseAsFatal;
        private volatile bool _qrDeliveredThisConnection;
        private int _preSessionCloseStreak;

        public bool SuppressReconnect
        {
            get => _suppressReconnect;
            set => _suppressReconnect = value;
        }

        /// <summary>
        /// Latches a logged-out / bad-session outcome so a connect cannot clear
        /// <see cref="SuppressReconnect"/> and restart the reconnect storm.
        /// Cleared when the session wipe finishes (QR pairing) or a session initializes.
        /// </summary>
        public bool FatalSessionEnded
        {
            get => _fatalSessionEnded;
            set => _fatalSessionEnded = value;
        }

        /// <summary>True after success/session-initialized for the current socket.</summary>
        public bool SessionEstablishedThisConnection
        {
            get => _sessionEstablishedThisConnection;
            set => _sessionEstablishedThisConnection = value;
        }

        /// <summary>
        /// True between stream:error 515 (pair stage 1 done) and session-initialized (stage 2).
        /// During this window Registered is already true but closes are expected; never treat as logout.
        /// </summary>
        public bool PairingRestartPending
        {
            get => _pairingRestartPending;
            set => _pairingRestartPending = value;
        }

        /// <summary>
        /// When true, pre-session closes may escalate to fatal logout. False for QR / 515 stage-2.
        /// </summary>
        public bool CountPreSessionCloseAsFatal
        {
            get => _countPreSessionCloseAsFatal;
            set => _countPreSessionCloseAsFatal = value;
        }

        /// <summary>
        /// Whether this connection ever put a QR on screen. Tells a code that ran out of time
        /// apart from a pairing that never got one, which need different things said to them.
        /// </summary>
        public bool QrDeliveredThisConnection
        {
            get => _qrDeliveredThisConnection;
            set => _qrDeliveredThisConnection = value;
        }

        /// <summary>
        /// Consecutive closes after Noise open but before WA login success.
        /// Common for revoked companion sessions that drop without a journaled stream:error.
        /// </summary>
        public int PreSessionCloseStreak => Volatile.Read(ref _preSessionCloseStreak);

        public int IncrementPreSessionCloseStreak() =>
            Interlocked.Increment(ref _preSessionCloseStreak);

        public int ResetPreSessionCloseStreak() =>
            Interlocked.Exchange(ref _preSessionCloseStreak, 0);
    }
}
