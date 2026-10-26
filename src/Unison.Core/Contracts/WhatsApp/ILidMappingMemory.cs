using System.Collections.Generic;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Sync memory peek over LID↔PN pairs (the durable store is async).
    /// <c>JidAliasTable</c> consults this when its own map misses, so canonicalization
    /// can stay on the UI / decrypt path without awaiting. Writes dual-fire into the
    /// store so the two maps stay warm together (phase 3.7b).
    /// </summary>
    public interface ILidMappingMemory
    {
        /// <summary>
        /// Given a normalized JID (<c>@lid</c> or <c>@s.whatsapp.net</c>), returns the
        /// paired address when the memory cache knows it.
        /// </summary>
        bool TryGetPairedJid(string normalizedJid, out string pairedJid);

        /// <summary>
        /// Best-effort remember of a LID↔PN pair. Non LID/PN pairs are ignored.
        /// Fire-and-forget into durable storage is fine — callers stay sync.
        /// </summary>
        void RememberPair(string jidA, string jidB);

        /// <summary>
        /// Same as <see cref="RememberPair"/> for many entries in one store write
        /// (persistence restore / bulk import).
        /// </summary>
        void RememberPairs(IEnumerable<KeyValuePair<string, string>> pairs);
    }
}
