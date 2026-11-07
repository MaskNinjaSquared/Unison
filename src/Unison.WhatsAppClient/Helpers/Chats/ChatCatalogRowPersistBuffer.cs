// =============================================================================
// ChatCatalogRowPersistBuffer
//
// Coalesces PersistRows(1) storms (author-strip + tip + OnDemand LID sweeps)
// into one slice write. Timer arming stays on the UWP host.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ChatCatalogRowPersistBuffer
    {
        public const int DebounceMilliseconds = 350;

        private readonly object _gate = new object();
        private Dictionary<string, ChatItem> _pending;

        /// <summary>
        /// Merges rows by normalized JID (later wins). Returns pending count after merge.
        /// </summary>
        public int Enqueue(IList<ChatItem> chats)
        {
            if (chats == null || chats.Count == 0)
            {
                lock (_gate)
                {
                    return _pending == null ? 0 : _pending.Count;
                }
            }

            lock (_gate)
            {
                if (_pending == null)
                {
                    _pending = new Dictionary<string, ChatItem>(
                        StringComparer.OrdinalIgnoreCase);
                }

                for (int i = 0; i < chats.Count; i++)
                {
                    ChatItem chat = chats[i];
                    if (chat == null)
                    {
                        continue;
                    }

                    string key = JidHelper.Normalize(chat.JID);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    _pending[key] = chat;
                }

                return _pending.Count;
            }
        }

        /// <summary>Drains pending rows (empty list when idle).</summary>
        public IList<ChatItem> TakeAll()
        {
            Dictionary<string, ChatItem> taken;
            lock (_gate)
            {
                taken = _pending;
                _pending = null;
            }

            if (taken == null || taken.Count == 0)
            {
                return Array.Empty<ChatItem>();
            }

            var list = new List<ChatItem>(taken.Count);
            foreach (KeyValuePair<string, ChatItem> pair in taken)
            {
                if (pair.Value != null)
                {
                    list.Add(pair.Value);
                }
            }

            return list;
        }
    }
}
