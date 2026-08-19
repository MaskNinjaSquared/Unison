namespace Unison.Core.Models
{
    /// <summary>
    /// Delivery/send state for a persisted message row (SQLite INTEGER / tinyint).
    /// </summary>
    public enum MessageSendState
    {
        /// <summary>Incoming message — ticks do not apply.</summary>
        NotApplicable = 0,

        /// <summary>Outgoing, waiting for server ack.</summary>
        Pending = 1,

        /// <summary>Outgoing, server ack (single tick).</summary>
        Sent = 2,

        /// <summary>Outgoing, delivered to device (double tick).</summary>
        Delivered = 3,

        /// <summary>Outgoing, read.</summary>
        Read = 4,

        /// <summary>Outgoing send failed.</summary>
        Failed = 5
    }
}
