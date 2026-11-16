using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>Domain-only mapping into <see cref="ChatMessage"/>.</summary>
    public interface IChatMessageMapper
    {
        /// <summary>Maps a non-reaction content snapshot into a <see cref="ChatMessage"/>.</summary>
        ChatMessage MapIndividual(ChatMessageMapContext context, ChatMessageContentSnapshot content);

        /// <summary>Legacy rows persisted as <c>[Reaction] …</c> text before reaction modeling.</summary>
        bool IsLegacyReactionRow(ChatMessage message);
    }
}
