using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// One member added to a group from a <c>w:gp2</c> <c>add</c> notification
    /// (Baileys <c>GROUP_PARTICIPANT_ADD</c>).
    /// </summary>
    public sealed class GroupJoinNotifyRequest
    {
        public string GroupJid { get; set; }

        /// <summary>Participant JID from the wire (<c>jid</c> attr).</summary>
        public string ParticipantJid { get; set; }

        /// <summary>PN when the row is LID-addressed (<c>phone_number</c>).</summary>
        public string PhoneDigits { get; set; }

        /// <summary>Notification stanza id when present — stabilises synthetic message ids.</summary>
        public string NotificationId { get; set; }

        public DateTime TimestampUtc { get; set; }
    }
}
