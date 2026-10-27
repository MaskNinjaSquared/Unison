namespace Unison.Core.Models
{
    /// <summary>
    /// Conversation category for list/detail UI (not message preview kind).
    /// </summary>
    public enum ChatKind
    {
        /// <summary>1:1 chat with another person.</summary>
        Direct = 0,

        /// <summary>Group (@g.us).</summary>
        Group = 1,

        /// <summary>Chat with yourself (notes / “Message yourself”).</summary>
        Personal = 2,

        /// <summary>
        /// WhatsApp Community parent hub (<c>parent</c> on w:g2).
        /// Linked subgroups stay <see cref="Group"/> with <c>LinkedParentJid</c>;
        /// announce channels stay <see cref="Group"/> with <c>IsCommunityAnnounce</c>.
        /// </summary>
        Community = 3,

        /// <summary>
        /// WhatsApp Channel / newsletter (<c>@newsletter</c>). Read-only for subscribers;
        /// listed under the Channels / Canais section, not the main chat list.
        /// </summary>
        Channel = 4
    }
}
