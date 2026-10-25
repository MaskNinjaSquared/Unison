using System.Collections.Generic;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Live-session alias registration and self-identity heal after a usync pair is filed.
    /// </summary>
    /// <remarks>
    /// Shared by <c>IUsyncAliasHost</c> (contact usync) and
    /// <c>IJidAliasPersistence</c> (inbound / history pairs). Table filing stays in
    /// Core / WhatsAppClient; this port is only socket + auth.
    /// </remarks>
    public interface ISocketAliasRegistrar
    {
        /// <summary>Registers a LID/phone pair with the live socket, if any.</summary>
        void RegisterPair(string jidA, string jidB, string source);

        /// <summary>
        /// Registers many dual-write pairs in one Bridge Store call (bulk import flush).
        /// </summary>
        void RegisterPairs(IReadOnlyDictionary<string, string> aliases, string source);

        /// <summary>
        /// Heals or purges <c>Me</c> when a usync-accepted pair touches our identity.
        /// </summary>
        void HealOwnIdentityAfterUsync(string normalizedPn, string normalizedLid);
    }
}
