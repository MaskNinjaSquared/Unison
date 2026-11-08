// =============================================================================
// AliasFollowUpAvatarCap
//
// PreferFrugal used to RequestAvatarRefresh for every PN in one Normal hop
// (journal avatars=5180). Cap the pass; remainder stays queued for the next
// debounce.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.Helpers.Contacts
{
    public static class AliasFollowUpAvatarCap
    {
        public const int MaxTargetsPerFrugalPass = 48;

        public static int MaxTargets(bool preferFrugal) =>
            preferFrugal ? MaxTargetsPerFrugalPass : int.MaxValue;

        /// <summary>
        /// Splits <paramref name="targets"/> into this pass and the remainder to Restore.
        /// </summary>
        public static void Split(
            IReadOnlyList<string> targets,
            int maxTargets,
            out IReadOnlyList<string> take,
            out IReadOnlyList<string> remainder)
        {
            if (targets == null || targets.Count == 0)
            {
                take = Array.Empty<string>();
                remainder = Array.Empty<string>();
                return;
            }

            if (maxTargets <= 0 || targets.Count <= maxTargets)
            {
                take = targets;
                remainder = Array.Empty<string>();
                return;
            }

            var kept = new List<string>(maxTargets);
            var rest = new List<string>(targets.Count - maxTargets);
            for (int i = 0; i < targets.Count; i++)
            {
                if (i < maxTargets)
                {
                    kept.Add(targets[i]);
                }
                else
                {
                    rest.Add(targets[i]);
                }
            }

            take = kept;
            remainder = rest;
        }
    }
}
