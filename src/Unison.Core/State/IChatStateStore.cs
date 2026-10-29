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

        /// <summary>True while the saved chat rows are being read back into <see cref="Chats"/> at startup.</summary>
        bool IsLoadingPersistedChats { get; }

        /// <summary>Raised with the chat JID whose message list changed.</summary>
        event EventHandler<string> MessagesChanged;

        /// <summary>Raised when resolved display names changed, so lists can re-render labels.</summary>
        event EventHandler DisplayNamesChanged;

        /// <summary>
        /// Raised once per roster apply or flush, with the group and the participants that changed
        /// (empty = the whole list), so listeners can repaint only those authors.
        /// </summary>
        event EventHandler<GroupRosterChangedEventArgs> GroupRosterChanged;

        ChatItem FindChat(string jid);

        /// <summary>A snapshot ordered oldest to newest. Never null.</summary>
        IReadOnlyList<ChatMessage> GetMessages(string chatJid);

        /// <summary>
        /// Like <see cref="GetMessages"/>, but a conversation still held under its LID or phone
        /// alias is found through <paramref name="canonicalOf"/>. A copy; never null.
        /// </summary>
        List<ChatMessage> GetConversationMessages(string chatJid, Func<string, string> canonicalOf);

        int GetMessageCount(string chatJid);

        /// <summary>Adds or updates chats, matching on JID. Existing instances are updated in place.</summary>
        Task UpsertChatsAsync(IEnumerable<ChatItem> chats);

        Task RemoveChatAsync(string jid);

        /// <summary>
        /// Adds or replaces messages, de-duplicated by id and kept in timestamp order.
        /// </summary>
        Task UpsertMessagesAsync(string chatJid, IEnumerable<ChatMessage> messages);

        /// <summary>
        /// Adds only the messages the timeline lacks, on the UI thread, leaving the ones already
        /// held untouched. Raises nothing: the timeline is seeded, not changed for anyone watching.
        /// </summary>
        Task AddMissingMessagesAsync(string chatJid, IEnumerable<ChatMessage> messages);

        Task RemoveMessageAsync(string chatJid, string messageId);

        /// <summary>
        /// Announces that a conversation's messages changed through a path other than this
        /// contract, so subscribers see it the same way. Blank addresses are ignored.
        /// </summary>
        void NotifyMessagesChanged(string chatJid);

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

        /// <summary>
        /// Asks listeners to re-read display names without merging anything new. Used when an
        /// existing name becomes reachable under a newly learned LID/PN alias.
        /// </summary>
        void NotifyDisplayNamesChanged();

        /// <summary>
        /// Announces that a group's roster was applied, so labels resolved from participants can
        /// catch up. Call once per apply, after the members are in place.
        /// </summary>
        /// <param name="changedJids">Null or empty when the whole list was replaced.</param>
        void NotifyGroupRosterChanged(string groupJid, IReadOnlyList<string> changedJids = null);

        /// <summary>Drops everything. Used when the session is wiped.</summary>
        Task ClearAsync();
    }
}
