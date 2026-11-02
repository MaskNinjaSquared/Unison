// =============================================================================
// CommunityLinkedGroupSnapshot
//
// One linked subgroup from Baileys communityFetchLinkedGroups / sub_groups IQ.
// =============================================================================
namespace Unison.Core.Models
{
    public sealed class CommunityLinkedGroupSnapshot
    {
        public string Jid { get; set; }

        public string Subject { get; set; }

        public long CreationUnixSeconds { get; set; }

        public string Owner { get; set; }

        public int Size { get; set; }
    }
}
