using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Saving the chat list, the name tables and the address aliases to disk. The client still
    /// owns the debounce timer, which its shutdown path resets.
    /// </summary>
    public interface IChatCatalogPersistence
    {
        /// <summary>Saves the whole catalogue a few seconds from now; calls in between batch into one write.</summary>
        void Schedule();

        /// <summary>
        /// Writes only these chat-list rows, now. For a badge or a single preview change, where
        /// <see cref="Schedule"/> would also rewrite every name and alias.
        /// </summary>
        void PersistRows(IList<ChatItem> chats);
    }
}
