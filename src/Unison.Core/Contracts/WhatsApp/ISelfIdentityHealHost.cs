// =============================================================================
// ISelfIdentityHealHost
//
// Auth-side heal/purge when usync files a pair that touches Me. Socket alias
// registration stays on ISocketAliasRegistrar.
// =============================================================================
namespace Unison.Core.Contracts.WhatsApp
{
    public interface ISelfIdentityHealHost
    {
        /// <summary>
        /// Heals or purges <c>Me</c> when a usync-accepted pair touches our identity.
        /// </summary>
        void HealOwnIdentityAfterUsync(string normalizedPn, string normalizedLid);
    }
}
