using System;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Local wipe and refill primitives the history façade needs from the client that still
    /// owns the in-memory conversation caches and the live socket's app-state reporter.
    /// </summary>
    /// <remarks>
    /// UI goes through <see cref="IHistoryService.ResyncConversationsAsync"/> /
    /// <see cref="IMessageService.ResyncConversationsAsync"/>. The client method of the same
    /// name is only the no-socket fallback the façade calls when it cannot ask the phone itself.
    /// </remarks>
    public interface IConversationResyncHost
    {
        /// <summary>
        /// Full wipe + FULL_HISTORY request used when the façade has no socket to drive the
        /// wait itself.
        /// </summary>
        Task ResyncConversationsAsync(IProgress<ConversationResyncPhase> progress = null);

        /// <summary>
        /// Drops in-memory chats, timelines and on-demand bookkeeping after the durable stores
        /// have been wiped.
        /// </summary>
        Task ClearConversationCachesAsync();

        /// <summary>
        /// Forces app-state to re-report pin/mute/archive so a rebuilt list is not left blank
        /// while LT-hash still says everything is current.
        /// </summary>
        Task ResyncChatListAppStateFlagsAsync();
    }
}
