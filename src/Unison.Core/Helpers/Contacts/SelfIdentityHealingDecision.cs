// =============================================================================
// SelfIdentityHealingDecision
//
// When usync reports a PN/LID pair that touches the logged-in account, whether
// to heal Me.Id, purge a foreign mapping, or do nothing.
//
// Written twice — once on the modern contact path and once inline in the legacy
// usync IQ parser. A mismatch here is not cosmetic: messages get signed under
// an identity recipients are not expecting. Decision only; persist / alias
// removal stay with the caller.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public enum SelfIdentityHealingAction
    {
        None,

        /// <summary>Me.Lid matched the usync user side; set Me.Id to the paired PN/LID.</summary>
        HealMeId,

        /// <summary>Me.Id matched the usync user side but the paired LID is foreign; revert and drop alias.</summary>
        PurgeForeignMapping
    }

    public static class SelfIdentityHealingDecision
    {
        /// <param name="normalizedUser">Normalized usync user JID (one side of the pair).</param>
        /// <param name="normalizedPair">Normalized other side of the pair.</param>
        /// <param name="meId">Current <c>Me.Id</c>.</param>
        /// <param name="normalizedMeLid">Normalized <c>Me.Lid</c>; empty means no decision.</param>
        public static SelfIdentityHealingAction Decide(
            string normalizedUser,
            string normalizedPair,
            string meId,
            string normalizedMeLid)
        {
            if (string.IsNullOrEmpty(normalizedMeLid) ||
                string.IsNullOrEmpty(normalizedUser) ||
                string.IsNullOrEmpty(normalizedPair))
            {
                return SelfIdentityHealingAction.None;
            }

            if (string.Equals(normalizedUser, normalizedMeLid, StringComparison.Ordinal) &&
                !string.Equals(normalizedPair, meId, StringComparison.Ordinal))
            {
                return SelfIdentityHealingAction.HealMeId;
            }

            if (string.Equals(normalizedUser, meId, StringComparison.Ordinal) &&
                !string.Equals(normalizedPair, normalizedMeLid, StringComparison.Ordinal))
            {
                return SelfIdentityHealingAction.PurgeForeignMapping;
            }

            return SelfIdentityHealingAction.None;
        }
    }
}
