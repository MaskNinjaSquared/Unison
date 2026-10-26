using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// ForgetChatStorage port on the compatibility client (name is historical —
    /// the only member is durable chat-delete I/O, not live app-state apply).
    /// </summary>
    /// <remarks>
    /// Wire + RAM halves live on the façades (<c>IChatService.DeleteChatAsync</c> /
    /// <c>ApplyAppStateDeleteChatAsync</c>, <c>IMessageService</c> pin/delete). This port
    /// only tombstones previews, drops SQLite history, and persists the catalog.
    /// </remarks>
    public interface IAppStateLocalHost
    {
        /// <summary>
        /// Durable half of a chat deletion: preview tombstone, history delete, catalog persist.
        /// RAM rows are removed by <c>IChatService.ApplyAppStateDeleteChatAsync</c> first.
        /// </summary>
        Task ForgetChatStorageAsync(string canonicalJid, IReadOnlyList<string> storeKeys);
    }
}
