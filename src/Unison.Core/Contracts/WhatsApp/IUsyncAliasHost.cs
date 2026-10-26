namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Files an accepted usync LID/phone pair and registers it with the live session.
    /// </summary>
    /// <remarks>
    /// Implemented in WhatsAppClient (<c>UsyncAliasHost</c>) over <c>UsyncAliasFiling</c>
    /// and <see cref="ISocketAliasRegistrar"/>. The connection client only implements the
    /// registrar (socket + auth heal).
    /// </remarks>
    public interface IUsyncAliasHost
    {
        /// <summary>
        /// Files the pair when policy accepts it.
        /// <paramref name="wasAlreadyAliased"/> is read before the write.
        /// </summary>
        bool TryFileUsyncAlias(string userJid, string lidJid, out bool wasAlreadyAliased);
    }
}
