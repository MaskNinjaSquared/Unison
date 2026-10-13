// =============================================================================
// GroupAvatarFallbackJids
//
// Which other groups to ask when this group has no picture of its own.
// The picture fetch stays in the client. This is only the address list.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Baileys.Protocol;

namespace Unison.Core.Helpers
{
    public static class GroupAvatarFallbackJids
    {
        private static readonly string[] ChildNames =
        {
            "linked_parent",
            "parent",
            "default_sub_group",
            "default_sub_community"
        };

        private static readonly string[] AttributeKeys = { "jid", "id", "parent", "linked_parent" };

        /// <summary>
        /// Parent and community addresses on a group node, in the order the wire names them.
        /// The group's own address is left out: that picture was already missing.
        /// </summary>
        public static List<string> From(BinaryNode group, string groupJid)
        {
            var candidates = new List<string>();
            if (group != null)
            {
                foreach (string childName in ChildNames)
                {
                    AddCandidate(candidates, group.GetChild(childName));
                }
            }

            string self = JidHelper.Normalize(groupJid);
            return candidates
                .Where(j => !string.IsNullOrWhiteSpace(j) &&
                            !string.Equals(JidHelper.Normalize(j), self, StringComparison.OrdinalIgnoreCase))
                .Select(JidHelper.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>A bare numeric id or an <c>@g.us</c> JID; anything else is not a group.</summary>
        public static string NormalizeGroupJid(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string value = raw.Trim();
            if (value.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
            {
                return JidHelper.Normalize(value);
            }

            if (value.IndexOf('@') < 0 && value.All(char.IsDigit))
            {
                return JidHelper.Normalize(value + "@g.us");
            }

            return null;
        }

        private static void AddCandidate(List<string> candidates, BinaryNode node)
        {
            if (node?.Attrs == null)
            {
                return;
            }

            foreach (string key in AttributeKeys)
            {
                string raw;
                if (!node.Attrs.TryGetValue(key, out raw))
                {
                    continue;
                }

                string jid = NormalizeGroupJid(raw);
                if (!string.IsNullOrWhiteSpace(jid))
                {
                    candidates.Add(jid);
                }
            }
        }
    }
}
