// =============================================================================
// IChatAccountActions
//
// Account-wide chat mutations (pin / read / delete) that today go through the
// UWP bridge's app-state patch. Portable façades call this port; the adapter
// forwards to IWhatsAppSocket / SocketBridge.
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Socket.AppState;
using Unison.Socket.UseCases.Messages;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>Wire-side chat mutations shared across linked devices.</summary>
    public interface IChatAccountActions
    {
        /// <summary>Null when no live socket can accept the mutation.</summary>
        bool IsAvailable { get; }

        Task SetPinnedAsync(string jid, bool pinned);

        Task DeleteChatAsync(string jid, IEnumerable<RangeMessage> lastMessages);

        Task MarkChatReadAsync(string jid, IEnumerable<RangeMessage> lastMessages);

        Task MarkMessagesReadAsync(IEnumerable<ReceiptTarget> targets);
    }
}
