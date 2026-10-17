using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Collapses chat rows that are the same person and re-keys the in-memory timelines to the
    /// canonical address, in one UI pass.
    /// </summary>
    /// <remarks>
    /// The fold itself is <see cref="DuplicateChatFold"/>. This type owns the list mutation, the
    /// message-cache canonicalize and the row-index invalidate the client used to do around it.
    /// Persistence and the display-names event stay with the caller via <see cref="Applied"/>.
    /// </remarks>
    public sealed class ChatListDeduper
    {
        private readonly IDispatcher _ui;
        private readonly ChatStateStore _chats;
        private readonly ChatRowIndex _rows;
        private readonly JidAliasTable _aliases;
        private readonly DisplayNameTable _pushNames;

        public ChatListDeduper(
            IDispatcher ui,
            ChatStateStore chats,
            ChatRowIndex rows,
            JidAliasTable aliases,
            DisplayNameTable pushNames)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _rows = rows ?? throw new ArgumentNullException(nameof(rows));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _pushNames = pushNames ?? throw new ArgumentNullException(nameof(pushNames));
        }

        /// <summary>Raised inside the UI pass when at least one row merged or one cache key folded.</summary>
        public event EventHandler<ChatListDeduperResult> Applied;

        public async Task<ChatListDeduperResult> RunAsync(string reason)
        {
            var result = new ChatListDeduperResult();
            await _ui.RunAsync(() =>
            {
                List<ChatItem> snapshots = _chats.Chats
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                    .ToList();

                if (snapshots.Count < 2)
                {
                    foreach (ChatItem chat in snapshots)
                    {
                        chat.JID = _aliases.GetCanonicalJid(chat.JID);
                    }

                    _rows.Invalidate();
                }

                DuplicateChatFoldResult folded =
                    new DuplicateChatFold(_chats.MessagesByChat, _pushNames, _aliases).Run(snapshots);
                foreach (ChatItem secondary in folded.Removed)
                {
                    _chats.Chats.Remove(secondary);
                }

                result.Merged = folded.Removed.Count;
                if (result.Merged > 0)
                {
                    _rows.Invalidate();
                }

                result.NormalizedKeys = _chats.MessagesByChat.CanonicalizeKeys(_aliases.GetCanonicalJid);

                if (result.Merged > 0 || result.NormalizedKeys > 0)
                {
                    Applied?.Invoke(this, result);
                }
            }).ConfigureAwait(false);
            return result;
        }
    }

    /// <summary>How many list rows left and how many message-cache keys folded.</summary>
    public sealed class ChatListDeduperResult
    {
        public int Merged { get; set; }

        public int NormalizedKeys { get; set; }
    }
}
