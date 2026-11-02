// =============================================================================
// GroupJoinNotifyMap
//
// Pure map from a group participant list → GroupJoinNotifyRequest /
// GroupLeaveNotifyRequest rows. SocketBridge / session hosts call this; Core
// stays free of WaEvent types.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Groups
{
    public static class GroupJoinNotifyMap
    {
        /// <summary>
        /// Builds notify rows for an add-participants event. Empty / blank JIDs skipped.
        /// Returns empty list (never null) when nothing usable remains.
        /// </summary>
        public static IReadOnlyList<GroupJoinNotifyRequest> FromAddParticipants(
            string groupJid,
            IEnumerable<string> participants,
            // IDictionary: Socket GroupParticipantsUpdate.ParticipantPhones is that shape;
            // IDictionary does not implement IReadOnlyDictionary on this target.
            IDictionary<string, string> participantPhones,
            string notificationId,
            DateTime timestampUtc,
            string actorJid = null)
        {
            if (string.IsNullOrWhiteSpace(groupJid) || participants == null)
            {
                return Array.Empty<GroupJoinNotifyRequest>();
            }

            var requests = new List<GroupJoinNotifyRequest>();
            foreach (string participant in participants)
            {
                if (string.IsNullOrWhiteSpace(participant))
                {
                    continue;
                }

                string phone = null;
                if (participantPhones != null)
                {
                    participantPhones.TryGetValue(participant, out phone);
                }

                requests.Add(new GroupJoinNotifyRequest
                {
                    GroupJid = groupJid,
                    ParticipantJid = participant,
                    PhoneDigits = phone,
                    ActorJid = actorJid,
                    NotificationId = notificationId,
                    TimestampUtc = timestampUtc
                });
            }

            return requests.Count == 0
                ? (IReadOnlyList<GroupJoinNotifyRequest>)Array.Empty<GroupJoinNotifyRequest>()
                : requests;
        }

        /// <summary>Same map for leave/remove (<c>w:gp2</c> remove).</summary>
        public static IReadOnlyList<GroupLeaveNotifyRequest> FromRemoveParticipants(
            string groupJid,
            IEnumerable<string> participants,
            IDictionary<string, string> participantPhones,
            string notificationId,
            DateTime timestampUtc,
            string actorJid = null)
        {
            if (string.IsNullOrWhiteSpace(groupJid) || participants == null)
            {
                return Array.Empty<GroupLeaveNotifyRequest>();
            }

            var requests = new List<GroupLeaveNotifyRequest>();
            foreach (string participant in participants)
            {
                if (string.IsNullOrWhiteSpace(participant))
                {
                    continue;
                }

                string phone = null;
                if (participantPhones != null)
                {
                    participantPhones.TryGetValue(participant, out phone);
                }

                requests.Add(new GroupLeaveNotifyRequest
                {
                    GroupJid = groupJid,
                    ParticipantJid = participant,
                    PhoneDigits = phone,
                    ActorJid = actorJid,
                    NotificationId = notificationId,
                    TimestampUtc = timestampUtc
                });
            }

            return requests.Count == 0
                ? (IReadOnlyList<GroupLeaveNotifyRequest>)Array.Empty<GroupLeaveNotifyRequest>()
                : requests;
        }
    }
}
