namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Files an accepted usync LID/phone pair with the live session and heals our own
    /// identity when the pair touches it.
    /// </summary>
    /// <remarks>
    /// Table policy and the two-way write are <c>UsyncAliasFiling</c> in Core. This port is
    /// only the socket registration and auth-state heal the client still owns.
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
