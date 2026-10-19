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
        /// Replaces messages already held under the same id and appends the rest, then sorts and
        /// rebuilds the id index. Id-less messages are always appended.
        /// </summary>
        /// <returns>False when nothing was written.</returns>
        public bool Upsert(string chatJid, IEnumerable<ChatMessage> messages)
        {
            if (string.IsNullOrEmpty(chatJid) || messages == null)
            {
                return false;
            }

            List<ChatMessage> list = null;
            foreach (ChatMessage message in messages)
            {
                if (message == null)
                {
                    continue;
                }

                if (list == null)
                {
                    list = GetOrAdd(chatJid);
                }

                int index = IndexOfMessage(list, message.Id);
                if (index >= 0)
                {
                    list[index] = message;
                }
                else
                {
                    list.Add(message);
                }
            }

            if (list == null)
            {
                return false;
            }

            ChatMessageOrder.SortInPlace(list);
            Ids.Rebuild(chatJid, list);
            return true;
        }

        /// <summary>Drops the message with this id and forgets the id.</summary>
        /// <returns>False when the conversation does not hold it.</returns>
        public bool RemoveMessage(string chatJid, string messageId)
        {
            if (string.IsNullOrEmpty(messageId) ||
                !TryGet(chatJid, out List<ChatMessage> list) ||
                list == null)
            {
                return false;
            }

            int index = IndexOfMessage(list, messageId);
            if (index < 0)
            {
                return false;
            }

            list.RemoveAt(index);
            Ids.Remove(chatJid, messageId);
            return true;
        }

        /// <summary>
        /// Moves our message with this id forward to <paramref name="status"/> in every
        /// conversation holding it (an alias timeline can hold the same send). Each chat's
        /// <paramref name="policyForChat"/> decides the status actually written, and a status
        /// never moves backwards.
        /// </summary>
        /// <returns>The conversation and message of every change.</returns>
        public List<KeyValuePair<string, ChatMessage>> ApplyOutgoingStatus(
            string messageId,
            string status,
            Func<string, string, string> policyForChat)
        {
            var changed = new List<KeyValuePair<string, ChatMessage>>();
            if (string.IsNullOrWhiteSpace(messageId) || policyForChat == null)
            {
                return changed;
            }

            foreach (KeyValuePair<string, List<ChatMessage>> pair in Snapshot())
            {
                if (pair.Value == null)
                {
                    continue;
                }

                int index = IndexOfMessage(pair.Value, messageId);
                ChatMessage message = index < 0 ? null : pair.Value[index];
                if (message == null || !message.IsFromMe)
                {
                    continue;
                }

                string effective = policyForChat(pair.Key, status);
                if (!MessageStatusProgression.ShouldApply(message.Status, effective))
                {
                    continue;
                }

                message.Status = effective;
                changed.Add(new KeyValuePair<string, ChatMessage>(pair.Key, message));
            }

            return changed;
        }

        /// <summary>
        /// Folds every conversation held under a non-canonical key into the one
        /// <paramref name="canonicalOf"/> names, through <see cref="MergeInto"/>. A key whose
        /// canonical form is blank is left where it is.
        /// </summary>
        /// <returns>How many conversations were folded.</returns>
        public int CanonicalizeKeys(Func<string, string> canonicalOf)
        {
            if (canonicalOf == null)
            {
                throw new ArgumentNullException(nameof(canonicalOf));
            }

            int folded = 0;
            foreach (string key in ChatJids())
            {
                string canonical = canonicalOf(key);
                if (string.IsNullOrWhiteSpace(canonical))
                {
                    continue;
                }

                if (MergeInto(key, canonical))
                {
                    folded++;
                }
            }

            return folded;
        }

        /// <summary>
        /// A copy of the conversation's timeline. When nothing is held under the address itself,
        /// the first conversation whose canonical form matches is used, so a chat still kept under
        /// its LID or phone alias is found either way. Empty when there is none.
        /// </summary>
        public List<ChatMessage> CopyConversation(string chatJid, Func<string, string> canonicalOf)
        {
            if (canonicalOf == null)
            {
                throw new ArgumentNullException(nameof(canonicalOf));
            }

            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return new List<ChatMessage>();
            }

            string key = JidHelper.Normalize(chatJid);
            List<ChatMessage> messages;
            if (!TryGet(key, out messages) || messages == null)
            {
                string canonical = canonicalOf(key);
                foreach (KeyValuePair<string, List<ChatMessage>> pair in Snapshot())
                {
                    if (string.Equals(canonicalOf(pair.Key), canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        messages = pair.Value;
                        break;
                    }
                }
            }

            return messages != null ? new List<ChatMessage>(messages) : new List<ChatMessage>();
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
        /// Inserts like <see cref="Insert"/> unless the conversation already holds a message with
        /// this id. An id-less message is always inserted.
        /// </summary>
        /// <returns>False when the id was already held.</returns>
        public bool InsertIfAbsent(string chatJid, ChatMessage message, int capacity, DateTime nowUtc)
        {
            if (message == null)
            {
                return false;
            }

            if (TryGet(chatJid, out List<ChatMessage> held) && held != null && IndexOfMessage(held, message.Id) >= 0)
            {
                return false;
            }

            Insert(chatJid, message, capacity, nowUtc);
            return true;
        }

        /// <summary>
        /// Replaces the conversation's list with a fresh window: <paramref name="pending"/> (messages
        /// not yet on disk) overlaid by what is held now, the held copy winning by id, id-less
        /// messages from both kept. Sorted, trimmed to <paramref name="capacity"/>, ids rebuilt.
        /// </summary>
        /// <returns>The new list, which is the instance the cache now holds.</returns>
        public List<ChatMessage> RebuildWindow(string chatJid, IEnumerable<ChatMessage> pending, int capacity, DateTime nowUtc)
        {
            var window = new List<ChatMessage>();
            var byId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
            TryGet(chatJid, out List<ChatMessage> held);

            Overlay(pending, window, byId);
            Overlay(held, window, byId);

            window.AddRange(byId.Values);
            ChatMessageOrder.SortInPlace(window);
            ActiveChatMessageWindow.TrimInPlace(window, capacity, nowUtc);
            Set(chatJid, window);
            return window;
        }

        private static void Overlay(IEnumerable<ChatMessage> source, List<ChatMessage> idLess, Dictionary<string, ChatMessage> byId)
        {
            if (source == null)
            {
                return;
            }

            foreach (ChatMessage message in source)
            {
                if (message == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(message.Id))
                {
                    idLess.Add(message);
                }
                else
                {
                    byId[message.Id] = message;
                }
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
            return AddMissing(chatJid, messages, int.MaxValue);
        }

        /// <summary>
        /// The same, stopping once <paramref name="maxToAdd"/> new messages have entered. Messages
        /// already held do not count against the limit.
        /// </summary>
        public List<ChatMessage> AddMissing(string chatJid, IEnumerable<ChatMessage> messages, int maxToAdd)
        {
            var added = new List<ChatMessage>();
            List<ChatMessage> timeline = GetOrAdd(chatJid);
            if (messages == null || maxToAdd <= 0)
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
                    if (added.Count >= maxToAdd)
                    {
                        break;
                    }
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

        private static int IndexOfMessage(List<ChatMessage> list, string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return -1;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i]?.Id, id, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
