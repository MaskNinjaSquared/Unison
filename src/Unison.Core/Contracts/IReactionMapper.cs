using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Applies <see cref="PendingReaction"/> onto parent <see cref="ChatMessage.Reactions"/>.
    /// Proto → pending mapping lives on the WhatsAppClient payload mapper.
    /// </summary>
    public interface IReactionMapper
    {
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
