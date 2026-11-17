namespace Unison.WhatsAppClient.Io.Chats
{
    /// <summary>Client-owned mark-read: clear badge and (when connected) send receipts.</summary>
    public sealed class MarkChatReadRequest
    {
        public string ChatJid { get; set; }

        /// <summary>Unread count observed on the row when the user opened the chat.</summary>
        public int UnreadCount { get; set; }
    }
}
