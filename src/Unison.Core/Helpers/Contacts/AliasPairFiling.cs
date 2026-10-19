// =============================================================================
// AliasPairFiling
//
// How a LID/phone pair that AliasPairPolicy already accepted is written into
// the alias table.
//
// Two shapes, because the two sources do not carry the same authority. A live
// envelope or a usync answer is the server naming the pair, so both directions
// are written. Two chat rows found to be one person is our own inference, so it
// writes the LID side but does not repoint a phone address the server already
// tied to a LID.
// =============================================================================
using System;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    public static class AliasPairFiling
    {
        private const string LidDomain = "@lid";

        /// <summary>
        /// Files the pair both ways and answers whether the LID side learned something new.
        /// </summary>
        /// <remarks>
        /// Only the LID side is consulted for the answer, which is what the live path keys its
        /// follow-up work on (alias event, avatar refetch). The phone side is overwritten either way.
        /// </remarks>
        public static bool FileBothWays(JidAliasTable table, string lid, string pn)
        {
            if (table == null || string.IsNullOrEmpty(lid) || string.IsNullOrEmpty(pn))
            {
                return false;
            }

            bool changed = !table.TryGetValue(lid, out string existingPn) ||
                           JidHelper.Normalize(existingPn) != pn;
            table[lid] = pn;
            table[pn] = lid;
            return changed;
        }

        /// <summary>
        /// Files a pair inferred from merging two chat rows: always the LID side, and the phone
        /// side only when it does not already point at an <c>@lid</c>.
        /// </summary>
        public static void FileFromRowMerge(JidAliasTable table, string lid, string pn)
        {
            if (table == null || string.IsNullOrEmpty(lid) || string.IsNullOrEmpty(pn))
            {
                return;
            }

            table[lid] = pn;

            if (!table.TryGetValue(pn, out string existingReverse) ||
                string.IsNullOrWhiteSpace(existingReverse) ||
                !JidHelper.Normalize(existingReverse).EndsWith(LidDomain, StringComparison.OrdinalIgnoreCase))
            {
                table[pn] = lid;
            }
        }
    }
}
