using System.Collections.Generic;
using Unison.Core.Models;
using Proto;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Maps reaction envelopes onto parent <see cref="ChatMessage.Reactions"/> and applies batches.
    /// </summary>
    public interface IReactionMapper
    {
        PendingReaction MapFromReactionMessage(
            Message.Types.ReactionMessage reactionMessage,
            ChatMessageMapContext context);

        PendingReaction MapFromHistoryReaction(
            Reaction reaction,
            ChatMessageMapContext parentContext);

        /// <summary>
        /// Upsert/remove a single pending reaction on the matching parent in <paramref name="messages"/>.
        /// </summary>
        bool TryApply(IList<ChatMessage> messages, PendingReaction pending, out ChatMessage parent);

        /// <summary>Apply directly onto a known parent message.</summary>
        bool ApplyToMessage(ChatMessage parent, PendingReaction pending);

        /// <summary>Apply all pending reactions; returns parents that changed.</summary>
        IList<ChatMessage> Apply(IList<ChatMessage> messages, IEnumerable<PendingReaction> pending);
    }
}
