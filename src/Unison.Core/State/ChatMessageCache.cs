using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    /// <summary>
    /// The in-memory message list of each conversation, keyed case-insensitively.
    /// </summary>
    /// <remarks>
    /// Which conversations exist is guarded by a lock, so asking for one never meets a resize. The
    /// list handed back is not copied: the client inserts, sorts and trims it in place on the UI
    /// thread, and every one of those edits relies on seeing the same instance next time. Folding an
    /// alias into its conversation is <see cref="MergeInto"/>.
    /// Walking all conversations goes through <see cref="Snapshot"/> or <see cref="ChatJids"/>, so a
    /// loop may remove the conversation it is looking at.
    /// </remarks>
    public sealed class ChatMessageCache
    {
        private readonly object _gate = new object();

        private readonly Dictionary<string, List<ChatMessage>> _byChat =
            new Dictionary<string, List<ChatMessage>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Which ids each timeline holds. Dropped with its timeline on <see cref="Remove"/>,
        /// <see cref="Clear"/> and <see cref="MergeInto"/>, rebuilt on <see cref="Set"/>; edits made
        /// to a list directly still have to register or rebuild here.
        /// </summary>
        public MessageIdIndex Ids { get; } = new MessageIdIndex();

        /// <summary>The live id set of a conversation, built from its timeline the first time.</summary>
        public HashSet<string> KnownIds(string chatJid)
        {
            string key = JidHelper.Normalize(chatJid);
            return Ids.GetOrBuild(key, () => TryGet(key, out List<ChatMessage> list) ? list : null);
        }

        /// <summary>The conversation's list; throws <see cref="KeyNotFoundException"/> when there is none.</summary>
        public List<ChatMessage> this[string chatJid]
        {
            get
            {
                List<ChatMessage> messages;
                if (!TryGet(chatJid, out messages))
                {
                    throw new KeyNotFoundException(chatJid);
                }

                return messages;
            }
        }

        public bool Contains(string chatJid)
        {
            if (string.IsNullOrEmpty(chatJid))
            {
                return false;
            }

            lock (_gate)
            {
                return _byChat.ContainsKey(chatJid);
            }
        }

        public bool TryGet(string chatJid, out List<ChatMessage> messages)
        {
            if (string.IsNullOrEmpty(chatJid))
            {
                messages = null;
                return false;
            }

            lock (_gate)
            {
                return _byChat.TryGetValue(chatJid, out messages);
            }
        }

        /// <summary>The conversation's list, created empty when there is none (or it is null).</summary>
        public List<ChatMessage> GetOrAdd(string chatJid)
        {
            if (string.IsNullOrEmpty(chatJid))
            {
                throw new ArgumentException("A conversation address is required.", nameof(chatJid));
            }

            lock (_gate)
            {
                List<ChatMessage> messages;
                if (!_byChat.TryGetValue(chatJid, out messages) || messages == null)
                {
                    messages = new List<ChatMessage>();
                    _byChat[chatJid] = messages;
                }

                return messages;
            }
        }

        public void Set(string chatJid, List<ChatMessage> messages)
        {
            if (string.IsNullOrEmpty(chatJid))
            {
                throw new ArgumentException("A conversation address is required.", nameof(chatJid));
            }

            lock (_gate)
            {
                _byChat[chatJid] = messages;
            }

            Ids.Rebuild(chatJid, messages);
        }

        public bool Remove(string chatJid)
        {
            if (string.IsNullOrEmpty(chatJid))
            {
                return false;
            }

            bool removed;
            lock (_gate)
            {
                removed = _byChat.Remove(chatJid);
            }

            Ids.RemoveChat(chatJid);
            return removed;
        }

        /// <summary>
        /// Moves an alias conversation's messages into the one it resolves to: only those the
        /// target lacks (by id, or by reference when there is none), kept in time order, and the
        /// alias is dropped. Edits the lists in place, so callers stay on the UI thread.
        /// </summary>
        /// <returns>False when the alias holds no list or is the target itself.</returns>
        public bool MergeInto(string aliasJid, string targetJid)
        {
            if (string.IsNullOrEmpty(aliasJid) ||
                string.Equals(aliasJid, targetJid, StringComparison.OrdinalIgnoreCase) ||
                !TryGet(aliasJid, out List<ChatMessage> alias) ||
                alias == null)
            {
                return false;
            }

            List<ChatMessage> target = GetOrAdd(targetJid);
            TransientChatMerge.AppendMissingMessages(target, alias.ToArray(), KnownIds(targetJid));
            ChatMessageOrder.SortInPlace(target);
            Remove(aliasJid);
            return true;
        }

        /// <summary>
        /// Puts a message in time order, trims the window to <paramref name="capacity"/> and
        /// registers the id — in that order, so an old message trimmed straight away still counts
        /// as seen. UI thread, like every in-place edit.
        /// </summary>
        public void Insert(string chatJid, ChatMessage message, int capacity, DateTime nowUtc)
        {
            ChatMessageOrder.InsertSorted(GetOrAdd(chatJid), message);
            Trim(chatJid, capacity, nowUtc);
            if (!string.IsNullOrEmpty(message?.Id))
            {
                KnownIds(chatJid).Add(message.Id);
            }
        }

        /// <summary>
        /// Adds the messages the timeline lacks (by id, or by reference when there is none) and
        /// keeps it in time order. Dedupes against the list itself rather than the index, which
        /// direct edits elsewhere can leave behind, and rebuilds the index afterwards.
        /// </summary>
        /// <returns>The messages that were added, in the order given.</returns>
        public List<ChatMessage> AddMissing(string chatJid, IEnumerable<ChatMessage> messages)
        {
            var added = new List<ChatMessage>();
            List<ChatMessage> timeline = GetOrAdd(chatJid);
            if (messages == null)
            {
                return added;
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChatMessage existing in timeline)
            {
                if (!string.IsNullOrWhiteSpace(existing?.Id))
                {
                    seenIds.Add(existing.Id);
                }
            }

            foreach (ChatMessage message in messages)
            {
                if (message == null)
                {
                    continue;
                }

                bool isNew = string.IsNullOrWhiteSpace(message.Id)
                    ? !timeline.Contains(message)
                    : seenIds.Add(message.Id);
                if (isNew)
                {
                    timeline.Add(message);
                    added.Add(message);
                }
            }

            ChatMessageOrder.SortInPlace(timeline);
            Ids.Rebuild(chatJid, timeline);
            return added;
        }

        /// <summary>
        /// Cuts the timeline to <paramref name="capacity"/> through
        /// <see cref="ActiveChatMessageWindow"/> and rebuilds its ids.
        /// </summary>
        /// <returns>False when the conversation holds no list.</returns>
        public bool Trim(string chatJid, int capacity, DateTime nowUtc)
        {
            string key = JidHelper.Normalize(chatJid);
            if (string.IsNullOrWhiteSpace(key) || !TryGet(key, out List<ChatMessage> messages) || messages == null)
            {
                return false;
            }

            ActiveChatMessageWindow.TrimInPlace(messages, capacity, nowUtc);
            Ids.Rebuild(key, messages);
            return true;
        }

        public void Clear()
        {
            lock (_gate)
            {
                _byChat.Clear();
            }

            Ids.Clear();
        }

        public int CountFor(string chatJid)
        {
            List<ChatMessage> messages;
            return TryGet(chatJid, out messages) && messages != null ? messages.Count : 0;
        }

        public int TotalMessageCount()
        {
            int total = 0;
            foreach (var pair in Snapshot())
            {
                if (pair.Value != null)
                {
                    total += pair.Value.Count;
                }
            }

            return total;
        }

        public List<string> ChatJids()
        {
            lock (_gate)
            {
                return new List<string>(_byChat.Keys);
            }
        }

        /// <summary>Every conversation and its (live, not copied) list, as of now.</summary>
        public KeyValuePair<string, List<ChatMessage>>[] Snapshot()
        {
            lock (_gate)
            {
                var pairs = new KeyValuePair<string, List<ChatMessage>>[_byChat.Count];
                int i = 0;
                foreach (var pair in _byChat)
                {
                    pairs[i++] = pair;
                }

                return pairs;
            }
        }
    }
}
