using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// One history-sync chunk ready for SQLite: listable rows plus reaction / pin / revoke side effects.
    /// </summary>
    public sealed class HistoryMessageWriteBatch
    {
        public List<HistoryMessage> Messages { get; } = new List<HistoryMessage>();

        public List<HistoryMessageReaction> Reactions { get; } = new List<HistoryMessageReaction>();

        public List<HistoryMessagePinUpdate> Pins { get; } = new List<HistoryMessagePinUpdate>();

        public List<HistoryMessageRevoke> Revokes { get; } = new List<HistoryMessageRevoke>();

        /// <summary>
        /// When true, live upsert replaces the reaction rows of <see cref="ReactionOwnerMessageIds"/>
        /// (history sync stays additive).
        /// </summary>
        public bool ReplaceExistingReactions { get; set; }

        /// <summary>
        /// Message ids whose reactions this batch is authoritative about. A timeline row loaded in
        /// chip-summary mode carries no reactor rows, so replacing by <see cref="Messages"/> would
        /// delete the stored reactions instead of rewriting them.
        /// </summary>
        public List<string> ReactionOwnerMessageIds { get; } = new List<string>();

        public bool IsEmpty
        {
            get
            {
                return Messages.Count == 0 &&
                       Reactions.Count == 0 &&
                       Pins.Count == 0 &&
                       Revokes.Count == 0;
            }
        }
    }
}
