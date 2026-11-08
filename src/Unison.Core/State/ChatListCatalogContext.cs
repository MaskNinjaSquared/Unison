// =============================================================================
// ChatListCatalogContext
//
// App-wide chat-list catalogue reads. Same API on frugal Mobile and desktop;
// callers apply ChatListCatalogAccess policies for when to rebuild / defer.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Unison.Core.Contracts;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class ChatListCatalogContext : IChatListCatalogContext
    {
        private readonly IChatStateStore _chats;
        private readonly Func<string, string> _peekCanonical;
        private readonly Func<long> _aliasGeneration;

        private readonly object _mapGate = new object();
        private Dictionary<string, ChatItem> _canonicalMap;
        private int _mapChatsCount = -1;
        private long _mapAliasGeneration = -1;

        public ChatListCatalogContext(
            IChatStateStore chats,
            Func<string, string> peekCanonical,
            Func<long> aliasGeneration = null)
        {
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _peekCanonical = peekCanonical ?? throw new ArgumentNullException(nameof(peekCanonical));
            _aliasGeneration = aliasGeneration ?? (() => 0);

            if (_chats.Chats is INotifyCollectionChanged notify)
            {
                notify.CollectionChanged += (_, __) => InvalidateCanonicalMap();
            }
        }

        public IReadOnlyList<ChatItem> ForScope(ChatListScope scope)
        {
            var source = _chats.Chats;
            var result = new List<ChatItem>(Math.Min(32, source.Count));
            for (int i = 0; i < source.Count; i++)
            {
                ChatItem chat = source[i];
                if (ChatListCatalogAccess.MatchesScope(chat, scope))
                {
                    result.Add(chat);
                }
            }

            return result;
        }

        public string PeekCanonical(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return jid;
            }

            string canonical = _peekCanonical(jid);
            return string.IsNullOrWhiteSpace(canonical) ? jid : canonical;
        }

        public IReadOnlyDictionary<string, ChatItem> GetCanonicalMap()
        {
            lock (_mapGate)
            {
                EnsureCanonicalMapLocked();
                return _canonicalMap;
            }
        }

        public ChatItem ResolveByCanonical(string jid)
        {
            string key = PeekCanonical(jid);
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            IReadOnlyDictionary<string, ChatItem> map = GetCanonicalMap();
            ChatItem hit;
            return map.TryGetValue(key, out hit) ? hit : null;
        }

        private void InvalidateCanonicalMap()
        {
            lock (_mapGate)
            {
                _canonicalMap = null;
                _mapChatsCount = -1;
                _mapAliasGeneration = -1;
            }
        }

        private void EnsureCanonicalMapLocked()
        {
            int count = _chats.Chats.Count;
            long generation = _aliasGeneration();
            if (_canonicalMap != null &&
                _mapChatsCount == count &&
                _mapAliasGeneration == generation)
            {
                return;
            }

            var map = new Dictionary<string, ChatItem>(
                Math.Max(16, count),
                StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                ChatItem chat = _chats.Chats[i];
                if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                string key = PeekCanonical(chat.JID);
                if (string.IsNullOrWhiteSpace(key) || map.ContainsKey(key))
                {
                    continue;
                }

                map[key] = chat;
            }

            _canonicalMap = map;
            _mapChatsCount = count;
            _mapAliasGeneration = generation;
        }
    }
}
