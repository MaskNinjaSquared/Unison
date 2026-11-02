// =============================================================================
// GroupLeaveNotifyRequest
//
// One participant leave/remove from a w:gp2 notification (Baileys remove action).
// =============================================================================
using System;

namespace Unison.Core.Models
{
    public sealed class GroupLeaveNotifyRequest
    {
        public string GroupJid { get; set; }

        public string ParticipantJid { get; set; }

        public string PhoneDigits { get; set; }

        /// <summary>
        /// Who removed the participant when named. Blank/same as participant → left;
        /// distinct → "{0} removed {1}".
        /// </summary>
        public string ActorJid { get; set; }

        public string NotificationId { get; set; }

        public DateTime TimestampUtc { get; set; }
    }
}
