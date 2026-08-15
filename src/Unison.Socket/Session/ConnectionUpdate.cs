// =============================================================================
// ConnectionUpdate
//
// The payload of the ConnectionUpdate event: a partial snapshot where null means
// "unchanged". The QR travels on this event rather than on one of its own, as in
// rc14, so a single subscription drives the whole login screen - connecting,
// QR shown, QR rotated, paired, open, closed.
//
// Ports: rc14 ConnectionState in src/Types/State.ts ('connection.update')
// =============================================================================
using System;

namespace Unison.Socket.Session
{
    /// <summary>Where the session is in its lifecycle.</summary>
    public enum ConnectionStatus
    {
        Connecting,
        Open,
        Close
    }

    /// <summary>Details of the last close, so the host can choose how to recover.</summary>
    public sealed class DisconnectInfo
    {
        public Exception Error { get; set; }

        public DateTimeOffset Date { get; set; }

        /// <summary>Parsed stream error code when the server supplied one.</summary>
        public DisconnectReason? Reason { get; set; }
    }

    /// <summary>
    /// Partial connection state. Null means "unchanged", matching the rc14 habit of emitting
    /// only the fields that moved. The QR travels here rather than on its own event, as in rc14.
    /// </summary>
    public sealed class ConnectionUpdate
    {
        public ConnectionStatus? Connection { get; set; }

        public string Qr { get; set; }

        public bool? IsNewLogin { get; set; }

        public bool? IsOnline { get; set; }

        public bool? ReceivedPendingNotifications { get; set; }

        public DisconnectInfo LastDisconnect { get; set; }
    }
}
