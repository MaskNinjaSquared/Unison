using Proto;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Mappers
{
    /// <summary>Proto reaction detect + domain <see cref="IChatMessageMapper"/>.</summary>
    public interface IChatMessagePayloadMapper : IChatMessageMapper
    {
        /// <summary>
        /// When the payload is a reaction envelope, fills <paramref name="reaction"/> and returns true
        /// (caller must not create a timeline message).
        /// </summary>
        bool TryMapReaction(Message message, ChatMessageMapContext context, out PendingReaction reaction);
    }
}
