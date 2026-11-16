using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Builds <see cref="ChatMessage"/> from domain snapshots (no proto).
    /// Proto reaction envelopes are mapped on the WhatsAppClient payload mapper.
    /// </summary>
    public interface IChatMessageMapper
    {
        /// <summary>Maps a non-reaction content snapshot into a <see cref="ChatMessage"/>.</summary>
        ChatMessage MapIndividual(ChatMessageMapContext context, ChatMessageContentSnapshot content);

        /// <summary>Legacy rows persisted as <c>[Reaction] …</c> text before reaction modeling.</summary>
        bool IsLegacyReactionRow(ChatMessage message);
    }
}
