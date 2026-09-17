using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    /// <summary>
    /// Which message ids a conversation already holds, so the same message arriving twice is only
    /// shown once.
    /// </summary>
    /// <remarks>
    /// A message reaches the timeline from several directions — live delivery, offline replay,
    /// history sync, and a merge of two addresses for the same contact — and the id is the only
    /// thing common to all of them. The index was four hand-written copies of the same
    /// "collect the ids of these messages" loop plus a raw dictionary the client reached into from
    /// a dozen places; three of the four read <c>m.Id</c> without checking <c>m</c> first.
    ///
    /// Addresses are normalised on the way in. A caller that forgot to do that got a second, empty
    /// index for a conversation that already had one, which reads as "this message is new" for
    /// every message in it.
    /// </remarks>
    public sealed class MessageIdIndex
    {
        // Ids are server-assigned and case-significant; the conversation key is not.
        private readonly Dictionary<string, HashSet<string>> _idsByChat =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        public bool Contains(string chatJid, string messageId)
        {
            if (string.IsNullOrEmpty(messageId))
            {
                return false;
            }

            string key = Key(chatJid);
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            HashSet<string> ids;
            return _idsByChat.TryGetValue(key, out ids) && ids.Contains(messageId);
        }

        public void Register(string chatJid, string messageId)
        {
            if (string.IsNullOrEmpty(messageId))
            {
                return;
            }

            string key = Key(chatJid);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            Bucket(key).Add(messageId);
        }

        public void Remove(string chatJid, string messageId)
        {
            if (string.IsNullOrEmpty(messageId))
            {
                return;
            }

            string key = Key(chatJid);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            HashSet<string> ids;
            if (_idsByChat.TryGetValue(key, out ids))
            {
                ids.Remove(messageId);
            }
        }

        public void RemoveChat(string chatJid)
        {
            string key = Key(chatJid);
            if (!string.IsNullOrEmpty(key))
            {
                _idsByChat.Remove(key);
            }
        }

        public void Clear()
        {
            _idsByChat.Clear();
        }

        /// <summary>
        /// Replaces what is held for a conversation. Used after a trim or a reload, where an id no
        /// longer in the conversation must stop counting as present.
        /// </summary>
        public void Rebuild(string chatJid, IEnumerable<ChatMessage> messages)
        {
            string key = Key(chatJid);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            _idsByChat[key] = Collect(messages);
        }

        /// <summary>
        /// The live set for a conversation, built from <paramref name="loadMessages"/> the first
        /// time it is asked for.
        /// </summary>
        /// <remarks>
        /// Handing out the real set rather than a copy is deliberate: the transient-chat merge adds
        /// to it as it moves messages onto their canonical address, and a copy would record that
        /// nowhere.
        /// </remarks>
        public HashSet<string> GetOrBuild(string chatJid, Func<IEnumerable<ChatMessage>> loadMessages)
        {
            string key = Key(chatJid);
            if (string.IsNullOrEmpty(key))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            HashSet<string> ids;
            if (_idsByChat.TryGetValue(key, out ids))
            {
                return ids;
            }

            ids = Collect(loadMessages == null ? null : loadMessages());
            _idsByChat[key] = ids;
            return ids;
        }

        private HashSet<string> Bucket(string key)
        {
            HashSet<string> ids;
            if (!_idsByChat.TryGetValue(key, out ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                _idsByChat[key] = ids;
            }

            return ids;
        }

        private static HashSet<string> Collect(IEnumerable<ChatMessage> messages)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (messages == null)
            {
                return ids;
            }

            foreach (ChatMessage message in messages)
            {
                if (message != null && !string.IsNullOrWhiteSpace(message.Id))
                {
                    ids.Add(message.Id);
                }
            }

            return ids;
        }

        private static string Key(string chatJid)
        {
            return string.IsNullOrWhiteSpace(chatJid) ? null : JidHelper.Normalize(chatJid);
        }
    }
}
