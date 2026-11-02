// =============================================================================
// GroupSubjectChangeNotifyRequest
//
// Group rename from groups.update / upsert (Baileys GROUP_CHANGE_SUBJECT).
// =============================================================================
using System;

namespace Unison.Core.Models
{
    public sealed class GroupSubjectChangeNotifyRequest
    {
        public string GroupJid { get; set; }

        public string NewSubject { get; set; }

        /// <summary>Who renamed the group when the server names an author.</summary>
        public string AuthorJid { get; set; }

        public string NotificationId { get; set; }

        public DateTime TimestampUtc { get; set; }

        /// <summary>
        /// True for live <c>groups.update</c> renames. False for <c>groups.upsert</c>
        /// (new group listing) so the catalog updates without a fake rename line.
        /// </summary>
        public bool EmitTimelineLine { get; set; }
    }
}
