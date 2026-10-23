// =============================================================================
// SelfIdentityAliasPair
//
// Whether the account's PN and LID form a bidirectional alias pair worth filing.
// Shared by auth-update and connection init so both stay in lockstep.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public static class SelfIdentityAliasPair
    {
        /// <summary>
        /// True when both ends are non-empty and distinct after the caller's normalize.
        /// </summary>
        public static bool TryCreate(
            string rawId,
            string rawLid,
            System.Func<string, string> normalize,
            out string id,
            out string lid)
        {
            id = null;
            lid = null;
            if (normalize == null || string.IsNullOrEmpty(rawId) || string.IsNullOrEmpty(rawLid))
            {
                return false;
            }

            id = normalize(rawId);
            lid = normalize(rawLid);
            return !string.IsNullOrEmpty(id) &&
                   !string.IsNullOrEmpty(lid) &&
                   !string.Equals(id, lid, System.StringComparison.Ordinal);
        }
    }
}
