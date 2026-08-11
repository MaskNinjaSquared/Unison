using Unison.Core.Models;
using Proto;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Maps WhatsApp proto payloads to <see cref="ChatMessage"/>.
    /// Reaction envelopes are skipped (returned as <see cref="PendingReaction"/>).
    /// </summary>
    public interface IChatMessageMapper
    {
        /// <summary>
        /// When the payload is a reaction envelope, fills <paramref name="reaction"/> and returns true
        /// (caller must not create a timeline message).
        /// </summary>
        bool TryMapReaction(Message message, ChatMessageMapContext context, out PendingReaction reaction);

        /// <summary>Maps a non-reaction content snapshot into a <see cref="ChatMessage"/>.</summary>
        ChatMessage MapIndividual(ChatMessageMapContext context, ChatMessageContentSnapshot content);

        /// <summary>Legacy rows persisted as <c>[Reaction] …</c> text before reaction modeling.</summary>
        bool IsLegacyReactionRow(ChatMessage message);
    }
}
