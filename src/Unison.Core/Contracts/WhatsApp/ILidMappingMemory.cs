namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Sync memory peek over LID↔PN pairs (the durable store is async).
    /// <c>JidAliasTable</c> consults this when its own map misses, so canonicalization
    /// can stay on the UI / decrypt path without awaiting.
    /// </summary>
    public interface ILidMappingMemory
    {
        /// <summary>
        /// Given a normalized JID (<c>@lid</c> or <c>@s.whatsapp.net</c>), returns the
        /// paired address when the memory cache knows it.
        /// </summary>
        bool TryGetPairedJid(string normalizedJid, out string pairedJid);
    }
}
