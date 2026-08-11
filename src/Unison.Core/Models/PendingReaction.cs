using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Reaction envelope awaiting attach to a parent <see cref="ChatMessage"/>.
    /// Produced by <c>IChatMessageMapper</c> / <c>IReactionMapper</c>; not a timeline row.
    /// </summary>
    public sealed class PendingReaction
    {
        public string TargetMessageId { get; set; }
        public string TargetChatJid { get; set; }
        public string ReactorJid { get; set; }
        public string ReactorName { get; set; }
        public string Emoji { get; set; }
        public string ReactionMessageId { get; set; }
        public DateTime Timestamp { get; set; }
        public bool FromMe { get; set; }

        /// <summary>Empty emoji means remove this reactor's reaction.</summary>
        public bool IsRemoval => string.IsNullOrWhiteSpace(Emoji);
    }
}
