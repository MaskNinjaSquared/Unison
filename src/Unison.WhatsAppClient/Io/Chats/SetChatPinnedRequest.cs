namespace Unison.WhatsAppClient.Io.Chats
{
    /// <summary>Client-owned chat list pin / unpin.</summary>
    public sealed class SetChatPinnedRequest
    {
        public string ChatJid { get; set; }

        public bool Pinned { get; set; }

        /// <summary>Prior local value, for rollback when the account patch fails.</summary>
        public bool PreviousPinned { get; set; }
    }
}
