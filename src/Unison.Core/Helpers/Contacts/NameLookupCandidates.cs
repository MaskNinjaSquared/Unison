// =============================================================================
// NameLookupCandidates
//
// Every address worth asking when a name might be stored under the phone, the
// LID, or the alias of either. Callers walk the set; the first hit wins.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace Unison.Core.Helpers
{
    public static class NameLookupCandidates
    {
        public static IEnumerable<string> Expand(
            IEnumerable<string> jids,
            Func<string, string> normalize,
            Func<string, string> aliasOf)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string jid in jids ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(jid))
                {
                    continue;
                }

                string normalized = normalize(jid);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                candidates.Add(normalized);
                string alias = aliasOf(normalized);
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    candidates.Add(normalize(alias));
                }

                string user = normalized.Split('@')[0];
                if (!string.IsNullOrEmpty(user))
                {
                    candidates.Add(user + "@s.whatsapp.net");
                    candidates.Add(user + "@lid");
                }
            }

            return candidates;
        }
    }
}
