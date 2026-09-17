namespace Unison.Core.Models
{
    /// <summary>
    /// Compact per-chat tip collected while offline replay keeps messages off the UI thread.
    /// Applied in a small batch so the list can update after the in-memory cache is released.
    /// </summary>
    public sealed class OfflineReplayChatSummary
    {
        public string Jid { get; set; }
        public string Preview { get; set; }
        public System.DateTime Timestamp { get; set; }
        public bool IsGroup { get; set; }
        public bool IsFromMe { get; set; }
        public int UnreadDelta { get; set; }
        public ChatPreviewKind Kind { get; set; }

        /// <summary>
        /// Delivery status of the tip message, so the list shows the ticks the message actually
        /// has after a replay rather than assuming "sent".
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Group author strip ("Name: "). Without this the list loses the prefix the live path
        /// already computed.
        /// </summary>
        public string AuthorPrefix { get; set; }
    }
}
