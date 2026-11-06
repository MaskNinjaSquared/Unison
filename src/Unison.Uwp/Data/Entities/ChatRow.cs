using System;
using SQLite;

namespace Unison.Uwp.Data.Entities
{
    /// <summary>
    /// SQLite row for local chat metadata.
    /// </summary>
    [Table("Chat")]
    public sealed class ChatRow
    {
        [PrimaryKey]
        public string Jid { get; set; }

        /// <summary><see cref="Unison.Core.Models.ChatStatus"/> as int.</summary>
        public int Status { get; set; }

        public bool IsChatPinned { get; set; }

        /// <summary>Unix seconds used to order pinned chats; zero is an explicit unpin.</summary>
        public long? PinnedTimestamp { get; set; }

        public bool IsWidgetPinned { get; set; }

        /// <summary>WhatsApp Favourites list (favoritesAction).</summary>
        public bool IsFavorite { get; set; }

        /// <summary>Unix seconds; null = not muted.</summary>
        public long? MutedUntil { get; set; }

        /// <summary><see cref="Unison.Core.Models.ChannelViewerRole"/> as int; null = never fetched.</summary>
        public int? ChannelRole { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
    }
}
