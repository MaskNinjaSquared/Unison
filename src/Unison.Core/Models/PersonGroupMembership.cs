using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// One group a <see cref="Person"/> belongs to (groups-in-common / membership index).
    /// </summary>
    public sealed class PersonGroupMembership
    {
        public string PersonJid { get; set; }
        public string GroupJid { get; set; }
        public GroupParticipantRole Role { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
