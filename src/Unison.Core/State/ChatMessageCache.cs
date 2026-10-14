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
        }

        public bool Remove(string chatJid)
        {
            if (string.IsNullOrEmpty(chatJid))
            {
                return false;
            }

            lock (_gate)
            {
                return _byChat.Remove(chatJid);
            }
        }

        /// <summary>
        /// Moves an alias conversation's messages into the one it resolves to: only those the
        /// target lacks (by id, or by reference when there is none), kept in time order, and the
        /// alias is dropped. Edits the lists in place, so callers stay on the UI thread.
        /// </summary>
        /// <param name="targetKnownIds">The target's id index, updated with what moved.</param>
        /// <returns>False when the alias holds no list or is the target itself.</returns>
        public bool MergeInto(string aliasJid, string targetJid, HashSet<string> targetKnownIds)
        {
            if (string.IsNullOrEmpty(aliasJid) ||
                string.Equals(aliasJid, targetJid, StringComparison.OrdinalIgnoreCase) ||
                !TryGet(aliasJid, out List<ChatMessage> alias) ||
                alias == null)
            {
                return false;
            }

            List<ChatMessage> target = GetOrAdd(targetJid);
            TransientChatMerge.AppendMissingMessages(target, alias.ToArray(), targetKnownIds);
            ChatMessageOrder.SortInPlace(target);
            Remove(aliasJid);
            return true;
        }

        public void Clear()
        {
            lock (_gate)
            {
                _byChat.Clear();
            }
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
