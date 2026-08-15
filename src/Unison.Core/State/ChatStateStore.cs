using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Unison.Core.Contracts;
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

        private readonly Dictionary<string, List<ChatMessage>> _messagesByChat =
            new Dictionary<string, List<ChatMessage>>(StringComparer.Ordinal);

        private readonly Dictionary<string, string> _pushNames =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly Dictionary<string, string> _addressBookNames =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public ChatStateStore(IDispatcher dispatcher)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            _dispatcher = dispatcher;
        }

        public ObservableCollection<ChatItem> Chats { get; } = new ObservableCollection<ChatItem>();

        public event EventHandler<string> MessagesChanged;

        public event EventHandler DisplayNamesChanged;

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
        // that used to own it. These four members disappear with the last direct
        // access - they are not part of IChatStateStore for that reason.
        //
        // One caveat while they exist. A writer coming through here does not take
        // the lock, so the snapshot the read methods return is protected by thread
        // affinity rather than by _gate: WhatsAppService mutates on the UI thread,
        // and so do the view models that read. Reading these collections from a
        // background thread is only safe once the writes come through this class.
        // ---------------------------------------------------------------------

        /// <summary>Transitional. Prefer <see cref="GetMessages"/> and <see cref="UpsertMessagesAsync"/>.</summary>
        public Dictionary<string, List<ChatMessage>> MessagesByChat
        {
            get { return _messagesByChat; }
        }

        /// <summary>Transitional. Names the network told us about.</summary>
        public Dictionary<string, string> PushNames
        {
            get { return _pushNames; }
        }

        /// <summary>Transitional. Names from the device address book.</summary>
        public Dictionary<string, string> AddressBookNames
        {
            get { return _addressBookNames; }
        }

        /// <summary>
        /// Transitional: lets a direct writer announce what it changed, so subscribers see the
        /// same events they will see once every write goes through this class.
        /// </summary>
        public void NotifyChangedExternally(string chatJid)
        {
            if (string.IsNullOrEmpty(chatJid))
            {
                RaiseDisplayNamesChanged();
            }
            else
            {
                RaiseMessagesChanged(chatJid);
            }
        }

        public ChatItem FindChat(string jid)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return null;
            }

            foreach (var chat in Chats)
            {
                if (string.Equals(chat.JID, jid, StringComparison.Ordinal))
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
                return _messagesByChat.TryGetValue(chatJid, out messages)
                    ? messages.ToArray()
                    : new ChatMessage[0];
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
                List<ChatMessage> messages;
                return _messagesByChat.TryGetValue(chatJid, out messages) ? messages.Count : 0;
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

            var changed = false;

            lock (_gate)
            {
                List<ChatMessage> list;
                if (!_messagesByChat.TryGetValue(chatJid, out list))
                {
                    list = new List<ChatMessage>();
                    _messagesByChat[chatJid] = list;
                }

                foreach (var message in messages)
                {
                    if (message == null)
                    {
                        continue;
                    }

                    var index = IndexOfMessage(list, message.Id);
                    if (index >= 0)
                    {
                        list[index] = message;
                    }
                    else
                    {
                        list.Add(message);
                    }

                    changed = true;
                }

                if (changed)
                {
                    list.Sort(CompareByTimestamp);
                }
            }

            if (changed)
            {
                RaiseMessagesChanged(chatJid);
            }

            return Task.FromResult(true);
        }

        public Task RemoveMessageAsync(string chatJid, string messageId)
        {
            if (string.IsNullOrEmpty(chatJid) || string.IsNullOrEmpty(messageId))
            {
                return Task.FromResult(true);
            }

            var changed = false;

            lock (_gate)
            {
                List<ChatMessage> list;
                if (_messagesByChat.TryGetValue(chatJid, out list))
                {
                    var index = IndexOfMessage(list, messageId);
                    if (index >= 0)
                    {
                        list.RemoveAt(index);
                        changed = true;
                    }
                }
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

            lock (_gate)
            {
                string name;
                if (_addressBookNames.TryGetValue(jid, out name) && !string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }

                if (_pushNames.TryGetValue(jid, out name) && !string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
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

        public Task ClearAsync()
        {
            lock (_gate)
            {
                _messagesByChat.Clear();
                _pushNames.Clear();
                _addressBookNames.Clear();
            }

            return _dispatcher.RunAsync(() => Chats.Clear());
        }

        private Task MergeNamesAsync(
            Dictionary<string, string> target,
            IEnumerable<KeyValuePair<string, string>> namesByJid)
        {
            if (namesByJid == null)
            {
                return Task.FromResult(true);
            }

            var changed = false;

            lock (_gate)
            {
                foreach (var pair in namesByJid)
                {
                    if (string.IsNullOrEmpty(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                    {
                        continue;
                    }

                    string current;
                    if (target.TryGetValue(pair.Key, out current) &&
                        string.Equals(current, pair.Value, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    target[pair.Key] = pair.Value;
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

        private static int IndexOfMessage(List<ChatMessage> list, string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return -1;
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Id, id, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int CompareByTimestamp(ChatMessage left, ChatMessage right)
        {
            return left.Timestamp.CompareTo(right.Timestamp);
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
            target.Timestamp = source.Timestamp;
            target.UnreadCount = source.UnreadCount;
            target.AvatarUrl = source.AvatarUrl;
            target.AvatarHighUrl = source.AvatarHighUrl;
        }
    }
}
