// =============================================================================
// GroupJoinNotifyMap
//
// Pure map from a group "add" participant list → GroupJoinNotifyRequest rows.
// SocketBridge / session hosts call this; Core stays free of WaEvent types.
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
            IReadOnlyDictionary<string, string> participantPhones,
            string notificationId,
            DateTime timestampUtc)
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
                    NotificationId = notificationId,
                    TimestampUtc = timestampUtc
                });
            }

            return requests.Count == 0
                ? (IReadOnlyList<GroupJoinNotifyRequest>)Array.Empty<GroupJoinNotifyRequest>()
                : requests;
        }
    }
}
