namespace Unison.WhatsAppClient.Io.Chats
{
    /// <summary>Client-owned chat favourite toggle.</summary>
    public sealed class SetChatFavoriteRequest
    {
        public string ChatJid { get; set; }

        public bool Favorite { get; set; }

        public bool PreviousFavorite { get; set; }
    }
}
