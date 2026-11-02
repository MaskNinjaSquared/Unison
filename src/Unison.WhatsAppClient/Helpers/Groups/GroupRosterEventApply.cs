using System;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Turns a <c>w:gp2</c> participants change into roster notes: add and promote/demote carry a
    /// role, modify carries the new address, remove drops every address the change names.
    /// </summary>
    public static class GroupRosterEventApply
    {
        public static void Apply(
            GroupRosterEvent change,
            Action<string, GroupMemberHint> note,
            Action<string, string, DateTime> remove)
        {
            if (change == null || string.IsNullOrWhiteSpace(change.GroupJid) || note == null || remove == null)
            {
                return;
            }

            foreach (GroupRosterEventParticipant participant in change.Participants)
            {
                if (participant == null || string.IsNullOrWhiteSpace(participant.Jid))
                {
                    continue;
                }

                if (change.Kind == GroupRosterEventKind.Remove)
                {
                    remove(change.GroupJid, participant.Jid, change.AtUtc);
                    if (!string.IsNullOrWhiteSpace(participant.PhoneJid))
                    {
                        remove(change.GroupJid, participant.PhoneJid, change.AtUtc);
                    }

                    continue;
                }

                note(change.GroupJid, new GroupMemberHint
                {
                    Jid = participant.Jid,
                    AltJid = participant.PhoneJid,
                    Role = RoleFor(change.Kind),
                    Source = change.Kind == GroupRosterEventKind.Add
                        ? GroupMemberHintSource.Join
                        : GroupMemberHintSource.RoleChange,
                    ObservedAtUtc = change.AtUtc
                });
            }
        }

        private static GroupParticipantRole? RoleFor(GroupRosterEventKind kind)
        {
            switch (kind)
            {
                case GroupRosterEventKind.Add:
                case GroupRosterEventKind.Demote:
                    return GroupParticipantRole.Member;
                case GroupRosterEventKind.Promote:
                    return GroupParticipantRole.Admin;
                default:
                    return null;
            }
        }
    }
}
