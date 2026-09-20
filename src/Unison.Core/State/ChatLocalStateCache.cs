// =============================================================================
// ChatLocalStateCache
//
// The in-memory half of the chat pin / mute store, and the thing ApplyTo reads.
//
// Pin and mute reach the app from two senders that do not know about each other
// - app-state patches and the history Conversation - and both write while a
// sync is running. That is why every operation here MERGES into whatever the
// cache currently holds instead of storing a value read earlier: a writer that
// takes its state, awaits SQLite, and then puts that copy back would undo any
// flag learned inside its own await, and the list would drop the icon on the
// next ApplyTo.
//
// Reads hand out copies. A caller holding the cached instance could otherwise
// edit the store by editing its own model.
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class ChatLocalStateCache
    {
        private readonly ConcurrentDictionary<string, ChatLocalState> _rows =
            new ConcurrentDictionary<string, ChatLocalState>(StringComparer.OrdinalIgnoreCase);

        public int Count
        {
            get { return _rows.Count; }
        }

        public bool Contains(string jid)
        {
            string key = Key(jid);
            return !string.IsNullOrEmpty(key) && _rows.ContainsKey(key);
        }

        /// <summary>A copy of what is stored, or null when this address has never been written.</summary>
        public ChatLocalState TryGet(string jid)
        {
            string key = Key(jid);
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            ChatLocalState stored;
            return _rows.TryGetValue(key, out stored) ? Clone(stored) : null;
        }

        /// <summary>
        /// Applies one change onto the current row, creating it when absent, and returns a copy of
        /// the merged result. This is both "remember this flag now" and "commit this write".
        /// </summary>
        public ChatLocalState Mutate(string jid, Action<ChatLocalState> mutate)
        {
            string key = Key(jid);
            if (string.IsNullOrEmpty(key) || mutate == null)
            {
                return null;
            }

            ChatLocalState merged = _rows.AddOrUpdate(
                key,
                _ =>
                {
                    var created = new ChatLocalState { Jid = key };
                    mutate(created);
                    return created;
                },
                (_, current) =>
                {
                    ChatLocalState next = Clone(current);
                    next.Jid = key;
                    mutate(next);
                    return next;
                });

            return Clone(merged);
        }

        /// <summary>
        /// Fills a row read from disk, and only when the cache has nothing for it. A cache miss
        /// sends the writer to SQLite; by the time that answer arrives a flag may already have been
        /// remembered, and the older row must not land on top of it.
        /// </summary>
        public void Seed(ChatLocalState state)
        {
            if (state == null)
            {
                return;
            }

            string key = Key(state.Jid);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            ChatLocalState seeded = Clone(state);
            seeded.Jid = key;
            _rows.TryAdd(key, seeded);
        }

        /// <summary>
        /// Replaces missing rows from disk and fills unknown fields, without undoing a flag the
        /// cache already learned. Warm runs while app-state / history are still remembering pins;
        /// overwriting those with a disk row that has not caught up yet was clearing the list.
        /// </summary>
        public void LoadWarm(IEnumerable<ChatLocalState> rows)
        {
            if (rows == null)
            {
                return;
            }

            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ChatLocalState row in rows)
            {
                if (row == null)
                {
                    continue;
                }

                string key = Key(row.Jid);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                ChatLocalState fromDisk = Clone(row);
                fromDisk.Jid = key;
                fromDisk.KnownFields = ChatLocalStateFields.All;

                _rows.AddOrUpdate(
                    key,
                    _ => fromDisk,
                    (_, current) => MergeWarm(current, fromDisk));

                loaded.Add(key);
            }

            foreach (string key in new List<string>(_rows.Keys))
            {
                if (!loaded.Contains(key))
                {
                    ChatLocalState dropped;
                    _rows.TryRemove(key, out dropped);
                }
            }
        }

        /// <summary>
        /// Disk fills fields the cache has not spoken about. Fields the cache already knows win —
        /// they may be newer than SQLite while a write is still in flight.
        /// </summary>
        private static ChatLocalState MergeWarm(ChatLocalState cached, ChatLocalState fromDisk)
        {
            ChatLocalState merged = Clone(cached);
            merged.Jid = fromDisk.Jid;

            if (!merged.Knows(ChatLocalStateFields.Pin))
            {
                merged.IsChatPinned = fromDisk.IsChatPinned;
            }

            if (!merged.Knows(ChatLocalStateFields.Mute))
            {
                merged.MutedUntil = fromDisk.MutedUntil;
            }

            if (!merged.Knows(ChatLocalStateFields.WidgetPin))
            {
                merged.IsWidgetPinned = fromDisk.IsWidgetPinned;
            }

            if (!merged.Knows(ChatLocalStateFields.Status))
            {
                merged.Status = fromDisk.Status;
            }

            return merged;
        }

        private static string Key(string jid)
        {
            string normalized = JidHelper.Normalize(jid);
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static ChatLocalState Clone(ChatLocalState source)
        {
            if (source == null)
            {
                return null;
            }

            var copy = new ChatLocalState { Jid = source.Jid };
            if (source.Knows(ChatLocalStateFields.Status))
            {
                copy.Status = source.Status;
            }

            if (source.Knows(ChatLocalStateFields.Pin))
            {
                copy.IsChatPinned = source.IsChatPinned;
            }

            if (source.Knows(ChatLocalStateFields.WidgetPin))
            {
                copy.IsWidgetPinned = source.IsWidgetPinned;
            }

            if (source.Knows(ChatLocalStateFields.Mute))
            {
                copy.MutedUntil = source.MutedUntil;
            }

            return copy;
        }
    }
}
