// =============================================================================
// AliasLinkedDirectChats
//
// Which direct-chat addresses to search when a message might already live
// under the contact's other identity. Groups are not aliases of a person.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace Unison.Core.Helpers
{
    public static class AliasLinkedDirectChats
    {
        public static IReadOnlyList<string> Collect(
            IEnumerable<string> expanded,
            Func<string, string> normalize,
            Func<string, string> canonical)
        {
            var chats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in expanded ?? Enumerable.Empty<string>())
            {
                string normalized = normalize(candidate);
                if (string.IsNullOrWhiteSpace(normalized) ||
                    normalized.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                chats.Add(normalized);
                chats.Add(canonical(normalized));
            }

            return chats
                .Where(chat => !string.IsNullOrWhiteSpace(chat))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
