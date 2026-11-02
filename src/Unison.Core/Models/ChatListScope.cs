namespace Unison.Core.Models
{
    public enum ChatListScope
    {
        /// <summary>Main chat list — active rows that are not Channels.</summary>
        Active = 0,

        /// <summary>Archived chats (any kind, including archived Channels).</summary>
        Archived = 1,

        /// <summary>WhatsApp Channels / Canais (<c>@newsletter</c>) that are still active.</summary>
        Channels = 2
    }
}
