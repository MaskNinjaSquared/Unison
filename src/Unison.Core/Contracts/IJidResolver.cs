using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// JID identity: folds the PN/LID pairs a session learns into one canonical id.
    /// </summary>
    /// <remarks>
    /// The same person reaches the app under a phone-number JID, a LID, and sometimes a
    /// device-suffixed variant of either. Everything that compares two JIDs - roster lookups,
    /// unread badges, mention overlays, avatar keys - has to agree on which of them is the real
    /// one, so that decision lives here rather than being repeated at each call site.
    /// </remarks>
    public interface IJidResolver
    {
        /// <summary>
        /// Canonical form of a JID. Returns the input unchanged when nothing is known about it.
        /// </summary>
        string GetCanonicalJid(string jid);

        /// <summary>
        /// The other half of a PN/LID pair, when the session has seen both. The key is matched as
        /// given, so callers that hold a raw JID should canonicalize it first.
        /// </summary>
        bool TryGetAlias(string jid, out string alias);

        /// <summary>The logged-in account, or null before pairing.</summary>
        Profile Self { get; }
    }
}
