using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.State
{
    /// <summary>
    /// The single owner of the in-memory chat, message and display-name state that the UI observes.
    /// </summary>
    /// <remarks>
    /// Today this state lives inside <c>WhatsAppService</c> as public fields, which is a large part
    /// of why that class cannot be broken up: every feature reaches into the same collections and
    /// nothing can be moved without moving all of it.
    /// <para>
    /// Concentrating it here inverts that. Facades write through this contract, view models read
    /// and observe it, and neither one has to know which socket stack produced the data. It also
    /// gives the rewrite somewhere to put state that is not the class it is trying to replace.
    /// </para>
    /// </remarks>
    public interface IChatStateStore
    {
        /// <summary>
        /// The chat list, safe to bind directly. Mutated only on the UI thread by the store.
        /// </summary>
        ObservableCollection<ChatItem> Chats { get; }

        /// <summary>Raised with the chat JID whose message list changed.</summary>
        event EventHandler<string> MessagesChanged;

        /// <summary>Raised when resolved display names changed, so lists can re-render labels.</summary>
        event EventHandler DisplayNamesChanged;

        ChatItem FindChat(string jid);

        /// <summary>A snapshot ordered oldest to newest. Never null.</summary>
        IReadOnlyList<ChatMessage> GetMessages(string chatJid);

        int GetMessageCount(string chatJid);

        /// <summary>Adds or updates chats, matching on JID. Existing instances are updated in place.</summary>
        Task UpsertChatsAsync(IEnumerable<ChatItem> chats);

        Task RemoveChatAsync(string jid);

        /// <summary>
        /// Adds or replaces messages, de-duplicated by id and kept in timestamp order.
        /// </summary>
        Task UpsertMessagesAsync(string chatJid, IEnumerable<ChatMessage> messages);

        Task RemoveMessageAsync(string chatJid, string messageId);

        /// <summary>Best known display name for a JID, or null when nothing is known yet.</summary>
        string ResolveDisplayName(string jid);

        /// <summary>
        /// Merges names learned from the network (push names, usync results).
        /// </summary>
        Task MergePushNamesAsync(IEnumerable<KeyValuePair<string, string>> namesByJid);

        /// <summary>
        /// Merges names from the device address book. These win over push names, because a user
        /// expects to see the name they saved rather than the one the contact chose.
        /// </summary>
        Task MergeAddressBookNamesAsync(IEnumerable<KeyValuePair<string, string>> namesByJid);

        /// <summary>Drops everything. Used when the session is wiped.</summary>
        Task ClearAsync();
    }
}
