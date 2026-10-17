using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    /// <summary>
    /// Default <see cref="IChatStateStore"/>: an observable chat list plus per-chat message lists
    /// and a display-name overlay.
    /// </summary>
    /// <remarks>
    /// Two invariants make this safe to bind to. Every mutation of <see cref="Chats"/> is marshalled
    /// to the UI thread through <see cref="IDispatcher"/>, because an <see cref="ObservableCollection{T}"/>
    /// changed off-thread crashes the list control. And every read of the message dictionary returns
    /// a snapshot under a lock, so a caller enumerating messages cannot be torn by an incoming
    /// message arriving mid-loop - the bug class that plagues the current implementation.
    /// </remarks>
    public sealed class ChatStateStore : IChatStateStore
    {
        private readonly IDispatcher _dispatcher;
        private readonly object _gate = new object();

        private readonly ChatMessageCache _messagesByChat = new ChatMessageCache();

        private readonly DisplayNameTable _pushNames = new DisplayNameTable();

        private readonly DisplayNameTable _addressBookNames = new DisplayNameTable();

        public ChatStateStore(IDispatcher dispatcher)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            _dispatcher = dispatcher;
        }

        public ObservableCollection<ChatItem> Chats { get; } = new ObservableCollection<ChatItem>();

        private volatile bool _isLoadingPersistedChats;

        public bool IsLoadingPersistedChats => _isLoadingPersistedChats;

        /// <summary>Marks the startup read of saved rows; dispose when it ends, failed or not.</summary>
        public IDisposable BeginPersistedChatsLoad()
        {
            _isLoadingPersistedChats = true;
            return new PersistedChatsLoad(this);
        }

        private sealed class PersistedChatsLoad : IDisposable
        {
            private ChatStateStore _owner;

            public PersistedChatsLoad(ChatStateStore owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                if (_owner != null)
                {
                    _owner._isLoadingPersistedChats = false;
                    _owner = null;
                }
            }
        }

        public event EventHandler<string> MessagesChanged;

        public event EventHandler DisplayNamesChanged;

        public event EventHandler<string> GroupRosterChanged;

        // ---------------------------------------------------------------------
        // Migration seams
        //
        // WhatsAppService has roughly 170 places that index these dictionaries
        // directly. Rewriting all of them in one change would mean rewriting the
        // sync and the display paths at once, with no way to tell which of the two
        // broke. So it points its own properties at the containers below and moves
        // to the methods above a call site at a time.
        //
        // The point of the exercise is already served: the state lives here, so a
        // view model can be pointed at this store without waiting for the class
        // that used to own it. MessagesByChat disappears with the last direct access
        // - it is not part of IChatStateStore for that reason, and WhatsAppService is
        // its only reader. The two name tables lock on their own and are not transitional.
        //
        // One caveat while MessagesByChat exists. It locks which conversations exist,
        // but the lists inside are edited in place without _gate, so the snapshot the
        // read methods return is protected by thread affinity: WhatsAppService mutates
        // on the UI thread, and so do the view models that read. Reading a message list
        // from a background thread is only safe once the writes come through this class.
        //
        // Keys are OrdinalIgnoreCase. MessageIdIndex already was, and JidHelper leaves
        // mixed-case @g.us alone — with Ordinal those two disagreed, so a group whose
        // casing drifted on the wire could be "already seen" in the index and missing
        // from the message list.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Transitional. Prefer <see cref="GetMessages"/> and <see cref="UpsertMessagesAsync"/>. Which
        /// conversations exist is locked; the lists inside are still edited in place on the UI thread.
        /// </summary>
        public ChatMessageCache MessagesByChat
        {
            get { return _messagesByChat; }
        }

        /// <summary>Names the network told us about. Locks on its own, so any thread may use it.</summary>
        public DisplayNameTable PushNames
        {
            get { return _pushNames; }
        }

        /// <summary>Names from the device address book. Locks on its own, so any thread may use it.</summary>
        public DisplayNameTable AddressBookNames
        {
            get { return _addressBookNames; }
        }

        public void NotifyMessagesChanged(string chatJid)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return;
            }

            RaiseMessagesChanged(chatJid);
        }

        public ChatItem FindChat(string jid)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return null;
            }

            foreach (var chat in Chats)
            {
                if (string.Equals(chat.JID, jid, StringComparison.OrdinalIgnoreCase))
                {
                    return chat;
                }
            }

            return null;
        }

        public IReadOnlyList<ChatMessage> GetMessages(string chatJid)
        {
            if (string.IsNullOrEmpty(chatJid))
            {
                return new ChatMessage[0];
            }

            lock (_gate)
            {
                List<ChatMessage> messages;
                return _messagesByChat.TryGet(chatJid, out messages) && messages != null
                    ? messages.ToArray()
                    : new ChatMessage[0];
            }
        }

        public List<ChatMessage> GetConversationMessages(string chatJid, Func<string, string> canonicalOf)
        {
            lock (_gate)
            {
                return _messagesByChat.CopyConversation(chatJid, canonicalOf);
            }
        }

        public int GetMessageCount(string chatJid)
        {
            if (string.IsNullOrEmpty(chatJid))
            {
                return 0;
            }

            lock (_gate)
            {
                return _messagesByChat.CountFor(chatJid);
            }
        }

        public Task UpsertChatsAsync(IEnumerable<ChatItem> chats)
        {
            if (chats == null)
            {
                return Task.FromResult(true);
            }

            var incoming = new List<ChatItem>(chats);
            if (incoming.Count == 0)
            {
                return Task.FromResult(true);
            }

            return _dispatcher.RunAsync(() =>
            {
                foreach (var chat in incoming)
                {
                    if (chat == null || string.IsNullOrEmpty(chat.JID))
                    {
                        continue;
                    }

                    var existing = FindChat(chat.JID);
                    if (existing == null)
                    {
                        Chats.Add(chat);
                    }
                    else if (!ReferenceEquals(existing, chat))
                    {
                        // Replacing the instance would reset every binding on the row, so the
                        // list keeps the object it already handed to the UI.
                        CopyInto(chat, existing);
                    }
                }
            });
        }

        public Task RemoveChatAsync(string jid)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return Task.FromResult(true);
            }

            lock (_gate)
            {
                _messagesByChat.Remove(jid);
            }

            return _dispatcher.RunAsync(() =>
            {
                var existing = FindChat(jid);
                if (existing != null)
                {
                    Chats.Remove(existing);
                }
            });
        }

        public Task UpsertMessagesAsync(string chatJid, IEnumerable<ChatMessage> messages)
        {
            if (string.IsNullOrEmpty(chatJid) || messages == null)
            {
                return Task.FromResult(true);
            }

            bool changed;
            lock (_gate)
            {
                changed = _messagesByChat.Upsert(chatJid, messages);
            }

            if (changed)
            {
                RaiseMessagesChanged(chatJid);
            }

            return Task.FromResult(true);
        }

        public Task AddMissingMessagesAsync(string chatJid, IEnumerable<ChatMessage> messages)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || messages == null)
            {
                return Task.FromResult(true);
            }

            var incoming = new List<ChatMessage>();
            foreach (ChatMessage message in messages)
            {
                if (message != null)
                {
                    incoming.Add(message);
                }
            }

            if (incoming.Count == 0)
            {
                return Task.FromResult(true);
            }

            string key = JidHelper.Normalize(chatJid);
            return _dispatcher.RunAsync(() =>
            {
                lock (_gate)
                {
                    _messagesByChat.AddMissing(key, incoming);
                }
            });
        }

        public Task RemoveMessageAsync(string chatJid, string messageId)
        {
            if (string.IsNullOrEmpty(chatJid) || string.IsNullOrEmpty(messageId))
            {
                return Task.FromResult(true);
            }

            bool changed;
            lock (_gate)
            {
                changed = _messagesByChat.RemoveMessage(chatJid, messageId);
            }

            if (changed)
            {
                RaiseMessagesChanged(chatJid);
            }

            return Task.FromResult(true);
        }

        public string ResolveDisplayName(string jid)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return null;
            }

            string name;
            if (_addressBookNames.TryGetValue(jid, out name) && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            if (_pushNames.TryGetValue(jid, out name) && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            return null;
        }

        public Task MergePushNamesAsync(IEnumerable<KeyValuePair<string, string>> namesByJid)
        {
            return MergeNamesAsync(_pushNames, namesByJid);
        }

        public Task MergeAddressBookNamesAsync(IEnumerable<KeyValuePair<string, string>> namesByJid)
        {
            return MergeNamesAsync(_addressBookNames, namesByJid);
        }

        public void NotifyDisplayNamesChanged()
        {
            RaiseDisplayNamesChanged();
        }

        public void NotifyGroupRosterChanged(string groupJid)
        {
            if (string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            var handler = GroupRosterChanged;
            if (handler != null)
            {
                handler(this, groupJid);
            }
        }

        public Task ClearAsync()
        {
            lock (_gate)
            {
                _messagesByChat.Clear();
            }

            _pushNames.Clear();
            _addressBookNames.Clear();

            return _dispatcher.RunAsync(() => Chats.Clear());
        }

        private Task MergeNamesAsync(
            DisplayNameTable target,
            IEnumerable<KeyValuePair<string, string>> namesByJid)
        {
            if (namesByJid == null)
            {
                return Task.FromResult(true);
            }

            var changed = false;

            foreach (var pair in namesByJid)
            {
                if (string.IsNullOrEmpty(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                if (target.Set(pair.Key, pair.Value))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseDisplayNamesChanged();
            }

            return Task.FromResult(true);
        }

        private void RaiseDisplayNamesChanged()
        {
            var handler = DisplayNamesChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void RaiseMessagesChanged(string chatJid)
        {
            var handler = MessagesChanged;
            if (handler != null)
            {
                handler(this, chatJid);
            }
        }

        /// <summary>
        /// Copies the fields the chat list renders. Deliberately partial: locally owned state such
        /// as mute and pin comes from <c>IChatStore</c> and must not be overwritten by a sync.
        /// </summary>
        private static void CopyInto(ChatItem source, ChatItem target)
        {
            target.Name = source.Name;
            target.LastMessage = source.LastMessage;
            target.LastMessageAuthor = source.LastMessageAuthor;
            target.LastMessageKind = source.LastMessageKind;
            target.LastMessageIsFromMe = source.LastMessageIsFromMe;
            target.LastMessageSendState = source.LastMessageSendState;
            target.LastMessageMentionedJids = source.LastMessageMentionedJids;
            target.LastMessageTimestampUtc = source.LastMessageTimestampUtc;
            target.LastMessageId = source.LastMessageId;
            target.Timestamp = source.Timestamp;
            target.UnreadCount = source.UnreadCount;
            target.AvatarUrl = source.AvatarUrl;
            target.AvatarHighUrl = source.AvatarHighUrl;
            if (source.IsGroup)
            {
                target.IsGroup = true;
            }
        }
    }
}
