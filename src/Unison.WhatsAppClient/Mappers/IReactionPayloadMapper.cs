using Proto;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Mappers
{
    /// <summary>Proto reaction envelopes → <see cref="PendingReaction"/>, plus domain apply.</summary>
    public interface IReactionPayloadMapper : IReactionMapper
    {
        PendingReaction MapFromReactionMessage(
            Message.Types.ReactionMessage reactionMessage,
            ChatMessageMapContext context);

        PendingReaction MapFromHistoryReaction(
            Reaction reaction,
            ChatMessageMapContext parentContext);
    }
}
