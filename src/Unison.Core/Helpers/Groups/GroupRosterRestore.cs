using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Puts the persisted roster on a group row that has none yet, so the Members pivot and
    /// mention lookup have something to show before the metadata query answers.
    /// </summary>
    public sealed class GroupRosterRestore
    {
        private const string GroupServer = "@g.us";

        private readonly IGroupNaming _naming;
        private readonly IDispatcher _ui;
        private readonly IJidResolver _jids;
        private readonly IChatStateStore _chats;
        private readonly IGroupRosterStore _store;

        public GroupRosterRestore(
            IGroupNaming naming,
            IDispatcher ui,
            IJidResolver jids,
            IChatStateStore chats,
            IGroupRosterStore store)
        {
            _naming = naming ?? throw new ArgumentNullException(nameof(naming));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public async Task EnsureLoadedAsync(string groupJid)
        {
            if (string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            string canonical = _jids.GetCanonicalJid(groupJid);
            if (string.IsNullOrWhiteSpace(canonical) ||
                !canonical.EndsWith(GroupServer, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ChatItem target = null;
            bool needsLoad = false;
            await _ui.RunAsync(() =>
            {
                target = _chats.Chats.FirstOrDefault(c =>
                    c != null &&
                    string.Equals(_jids.GetCanonicalJid(c.JID), canonical, StringComparison.OrdinalIgnoreCase));
                needsLoad = target != null && !target.HasGroupMembers;
            }).ConfigureAwait(false);

            if (!needsLoad || target == null)
            {
                return;
            }

            IReadOnlyList<GroupMember> stored;
            try
            {
                await _store.InitializeAsync().ConfigureAwait(false);
                stored = await _store.GetForGroupAsync(canonical).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupRosterRestore] Group roster store read failed: " + ex.Message);
                return;
            }

            List<GroupMember> hydrated = GroupRosterRules.HydrateFromStore(stored, _naming.ResolveMemberDisplayName);
            if (hydrated.Count == 0)
            {
                return;
            }

            await _ui.RunAsync(() =>
            {
                if (GroupRosterRules.ApplyRestored(target, hydrated))
                {
                    _chats.NotifyGroupRosterChanged(target.JID);
                }
            }).ConfigureAwait(false);

            Debug.WriteLine(
                "[GroupRosterRestore] Restored group roster from store count=" +
                hydrated.Count +
                " jid=" +
                canonical);
        }
    }
}
