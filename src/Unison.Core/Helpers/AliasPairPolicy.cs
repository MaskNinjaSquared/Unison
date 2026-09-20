// =============================================================================
// AliasPairPolicy
//
// Whether a LID/PN pair may be filed in the alias table.
//
// The table is what decides that two addresses are the same person, so a wrong
// entry does not produce a wrong label -- it merges two conversations. The case
// that matters is a pair filing some contact under the user's own identity:
// every message from that contact then lands in the self chat, and the contact's
// own row stops receiving anything.
//
// That guard has to be applied before the entry goes in. JidAliasTable checks
// afterwards, and by then the poisoned entry is itself the evidence that the
// contact is us, which switches the later check off.
//
// This existed in two places -- the live path and the startup restore -- written
// separately and not agreeing. See ReverseAliasMatches for where they differed.
// =============================================================================
using System;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    public static class AliasPairPolicy
    {
        private const string PhoneDomain = "@s.whatsapp.net";
        private const string LidDomain = "@lid";

        /// <summary>
        /// Whether the pair has the shape of a LID mapped to a phone address.
        /// </summary>
        /// <remarks>
        /// Filed both ways round, so the two sides are not interchangeable: reversing them
        /// would make the phone address resolve to the LID and undo every canonicalization
        /// that depends on the phone address being the canonical one.
        /// </remarks>
        public static bool IsWellFormedPair(string lid, string pn, JidAliasTable table)
        {
            if (string.IsNullOrEmpty(lid) || string.IsNullOrEmpty(pn) || table == null)
            {
                return false;
            }

            bool lidAccepted =
                lid.EndsWith(LidDomain, StringComparison.OrdinalIgnoreCase) ||
                table.IsLidLike(lid);

            // A LID-like address on the phone domain is still a LID, so it cannot stand on
            // the phone side of the pair.
            bool pnAccepted =
                pn.EndsWith(PhoneDomain, StringComparison.OrdinalIgnoreCase) &&
                !table.IsLidLike(pn);

            return lidAccepted && pnAccepted;
        }

        /// <summary>
        /// Works out which side of a pair is the LID and answers whether it may be filed.
        /// </summary>
        /// <remarks>
        /// Most callers learn a pair without being told which way round it is — a usync
        /// answer, a contact record, two chat rows found to be the same person. Leaving each
        /// of them to decide is how the guard came to be bypassed in four places, so the
        /// orientation is worked out here and a caller only has to ask.
        ///
        /// Note the asymmetry with the table: pairs are *filed* both ways, but they are only
        /// *valid* one way round, because only one of the two addresses can be canonical.
        /// </remarks>
        public static bool TryAcceptPair(
            string first,
            string second,
            JidAliasTable table,
            out string lid,
            out string pn)
        {
            lid = null;
            pn = null;

            string a = JidHelper.Normalize(first);
            string b = JidHelper.Normalize(second);

            if (IsWellFormedPair(a, b, table))
            {
                lid = a;
                pn = b;
            }
            else if (IsWellFormedPair(b, a, table))
            {
                lid = b;
                pn = a;
            }
            else
            {
                return false;
            }

            if (WouldPutAContactUnderOurIdentity(lid, pn, table))
            {
                lid = null;
                pn = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether filing this pair would put a contact under the user's own identity.
        /// </summary>
        /// <param name="lid">The side being filed as the LID — a contact, or the user.</param>
        /// <param name="pn">The side being filed as the phone address.</param>
        public static bool WouldPutAContactUnderOurIdentity(string lid, string pn, JidAliasTable table)
        {
            if (table == null || !table.IsSelfLinked(pn) || table.IsSelfLinked(lid))
            {
                return false;
            }

            // The user's own LID and phone address pointing at each other is the one
            // legitimate pair that reaches this far, and it must survive: dropping it would
            // cost the user the link to their own identity.
            return !ReverseAliasMatches(lid, pn, table);
        }

        /// <summary>
        /// Whether a pair restored from disk may be filed.
        /// </summary>
        /// <remarks>
        /// Stricter than the live check, because the two sources do not have the same
        /// authority. A usync answer is the server telling us something; a row read back
        /// from our own file is only telling us what we believed last time, and if what we
        /// believed was wrong it has no way to correct itself.
        ///
        /// The difference is the mirrored direction: our own LID paired with someone else's
        /// phone address. Live, that is how identity healing works — the server reports
        /// which number our LID belongs to, and <c>Me.Id</c> is corrected to match. From
        /// disk it is the same corruption running in reverse, and it spreads: once filed,
        /// that contact's number reads as ours, so the contact's own legitimate pair is then
        /// refused as poison and they are left with no alias at all.
        ///
        /// Order-agnostic, because a pair is written both ways and either entry can be the
        /// one the restore loop reaches first.
        /// </remarks>
        public static bool IsUnsafeToRestore(string first, string second, JidAliasTable table)
        {
            if (table == null)
            {
                return false;
            }

            string a = JidHelper.Normalize(first);
            string b = JidHelper.Normalize(second);

            return WouldPutAContactUnderOurIdentity(a, b, table) ||
                   WouldPutAContactUnderOurIdentity(b, a, table) ||
                   WouldRedefineOurOwnIdentity(a, b, table) ||
                   WouldRedefineOurOwnIdentity(b, a, table);
        }

        /// <summary>
        /// Whether the pair claims our own LID belongs to someone else's phone address.
        /// </summary>
        /// <remarks>
        /// Only meaningful once we know our own identity. Until then nothing can be said to
        /// contradict an identity we do not have yet, and the guard stays out of the way.
        ///
        /// Shape is deliberately not consulted. The restore path does not filter on shape —
        /// the file predates that validation and dropping unusual-but-harmless rows would
        /// cost real identities — so a malformed pair still reaches the table, and one that
        /// hangs our own address off someone else's is no less wrong for being malformed.
        /// </remarks>
        private static bool WouldRedefineOurOwnIdentity(string ours, string theirs, JidAliasTable table)
        {
            return table.IsSelfLinked(ours) && !table.IsSelfLinked(theirs);
        }

        /// <summary>
        /// Whether the table already records the phone address pointing back at this LID.
        /// </summary>
        /// <remarks>
        /// Both sides are reduced before comparing, and that is precisely where the two
        /// original copies of this rule disagreed. The user's own LID is often held in its
        /// dotted form on the phone domain (<c>123.45@s.whatsapp.net</c>) while the table
        /// records the plain <c>123@lid</c>. The live path reduced it before comparing; the
        /// startup restore compared the dotted form as-is, never matched, and therefore threw
        /// away the user's own legitimate alias on every launch that had one stored that way.
        ///
        /// Reducing only one side has the same bug on the other end of the comparison, since
        /// the dotted form is accepted on the LID side of a pair and so can be what was
        /// stored.
        /// </remarks>
        private static bool ReverseAliasMatches(string lid, string pn, JidAliasTable table)
        {
            string expected = ReduceToPlainLid(lid, table);

            if (!table.TryGetValue(pn, out string reverse))
            {
                return false;
            }

            string stored = ReduceToPlainLid(JidHelper.Normalize(reverse), table);

            return string.Equals(stored, expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reduces a dotted LID carried on the phone domain to the plain LID the table files
        /// it under. Anything else is returned unchanged.
        /// </summary>
        public static string ReduceToPlainLid(string jid, JidAliasTable table)
        {
            if (string.IsNullOrEmpty(jid) ||
                table == null ||
                !table.IsLidLike(jid) ||
                !jid.EndsWith(PhoneDomain, StringComparison.OrdinalIgnoreCase))
            {
                return jid;
            }

            string user = jid.Split('@')[0];
            int dot = user.IndexOf('.');

            // The dot separates the LID from a device suffix. Without one there is nothing
            // to reduce, and taking an empty prefix would match everything.
            return dot > 0 ? user.Substring(0, dot) + LidDomain : jid;
        }
    }
}
