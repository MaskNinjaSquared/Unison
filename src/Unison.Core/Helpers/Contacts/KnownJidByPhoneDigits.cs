using System.Collections.Generic;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Whether a number the user typed is already an alias or a stored contact key.
    /// </summary>
    /// <remarks>
    /// Alias pairs are walked first, key then value, then the name keys. The first address
    /// whose text starts with the digits wins. That prefix match is what the client already
    /// did: "11" hits every Brazilian number, and a later exact match never gets a turn.
    /// </remarks>
    public static class KnownJidByPhoneDigits
    {
        public static string Find(
            string digits,
            IEnumerable<KeyValuePair<string, string>> aliases,
            IEnumerable<string> namedJids)
        {
            if (string.IsNullOrEmpty(digits))
            {
                return null;
            }

            if (aliases != null)
            {
                foreach (KeyValuePair<string, string> alias in aliases)
                {
                    if (alias.Key != null && alias.Key.StartsWith(digits))
                    {
                        return alias.Key;
                    }

                    if (alias.Value != null && alias.Value.StartsWith(digits))
                    {
                        return alias.Value;
                    }
                }
            }

            if (namedJids != null)
            {
                foreach (string jid in namedJids)
                {
                    if (jid != null && jid.StartsWith(digits))
                    {
                        return jid;
                    }
                }
            }

            return null;
        }
    }
}
