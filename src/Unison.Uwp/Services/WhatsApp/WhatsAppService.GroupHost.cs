using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// What the group flows in Core — <see cref="GroupMetadataApply"/> and
    /// <see cref="GroupRosterRestore"/> — still need from the client.
    /// </summary>
    /// <remarks>
    /// Implemented explicitly so it adds nothing to the client's own surface; the only ways in
    /// are the <see cref="IGroupHost"/> registration and the applier the client builds for its
    /// own query passes.
    /// </remarks>
    public partial class WhatsAppService : IGroupHost
    {
        Task IGroupHost.RunOnUiThreadAsync(Action action) => _ui.RunAsync(action);

        string IGroupHost.ResolveMemberDisplayName(string jid) => ResolveGroupMemberDisplayName(jid);

        string IGroupHost.ResolveChatDisplayName(string jid) => ResolveDisplayName(jid, "chat");

        bool IGroupHost.IsMeaningfulGroupLabel(string label, string groupJid) =>
            IsMeaningfulChatLabel(label, groupJid, true);

        bool IGroupHost.HasMeaningfulGroupLabel(string groupJid, string rowName) =>
            HasMeaningfulGroupLabel(groupJid, rowName);

        string IGroupHost.FindGroupRowName(string groupJid) => FindGroupRowName(groupJid);

        void IGroupHost.CacheGroupSubject(string groupJid, string subject) => ContactNames.Set(groupJid, subject);

        string IGroupHost.FindExistingAvatarUrl(string jid, string phoneNumber, string lid) =>
            FindExistingAvatarUrl(jid, phoneNumber, lid);

        void IGroupHost.SchedulePersist() => SchedulePersist();

        void IGroupHost.SaveGroupRoster(string groupJid, List<GroupMember> snapshot)
        {
            if (snapshot == null || snapshot.Count == 0)
            {
                return;
            }

            _ = PersistGroupRosterAsync(groupJid, snapshot);
        }

        void IGroupHost.SaveGroupMemberships(string groupJid, List<PersonGroupMembership> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            _ = PersistGroupMembershipsAsync(groupJid, rows);
        }
    }
}
