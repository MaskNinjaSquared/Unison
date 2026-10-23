// =============================================================================
// UsyncAliasHost
//
// Files an accepted usync pair in the table, then asks the connection client to
// register it on the socket and heal Me when the pair touches our identity.
// =============================================================================
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>Default <see cref="IUsyncAliasHost"/> over filing + socket/auth ports.</summary>
    public sealed class UsyncAliasHost : IUsyncAliasHost
    {
        private readonly UsyncAliasFiling _filing;
        private readonly ISocketAliasRegistrar _socket;

        public UsyncAliasHost(UsyncAliasFiling filing, ISocketAliasRegistrar socket)
        {
            _filing = filing ?? throw new System.ArgumentNullException(nameof(filing));
            _socket = socket ?? throw new System.ArgumentNullException(nameof(socket));
        }

        /// <inheritdoc />
        public bool TryFileUsyncAlias(string userJid, string lidJid, out bool wasAlreadyAliased)
        {
            UsyncAliasFilingResult result = _filing.TryFile(userJid, lidJid);
            wasAlreadyAliased = result.WasAlreadyAliased;
            if (!result.Filed)
            {
                return false;
            }

            _socket.RegisterPair(result.AcceptedPn, result.AcceptedLid, "contact-usync");
            _socket.HealOwnIdentityAfterUsync(result.AcceptedPn, result.AcceptedLid);
            return true;
        }
    }
}
