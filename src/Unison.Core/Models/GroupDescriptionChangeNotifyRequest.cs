// =============================================================================
// GroupDescriptionChangeNotifyRequest
//
// Group description from groups.update / upsert (Baileys GROUP_CHANGE_DESCRIPTION).
//
// Deliberately separate from GroupSubjectChangeNotifyRequest: a description change
// carries no subject, and the subject path drops any update without one.
// =============================================================================
using System;

namespace Unison.Core.Models
{
    public sealed class GroupDescriptionChangeNotifyRequest
    {
        public string GroupJid { get; set; }

        /// <summary>
        /// The new description. Empty string means an admin cleared it. Never null — an
        /// update that said nothing about the description is not forwarded at all, because
        /// avatar notifications arrive as a group update with no description node.
        /// </summary>
        public string NewDescription { get; set; }

        /// <summary>Wire id of the new description; null when the server omitted it.</summary>
        public string DescriptionId { get; set; }

        /// <summary>Who edited the description when the server names an author.</summary>
        public string AuthorJid { get; set; }

        public DateTime TimestampUtc { get; set; }
    }
}
