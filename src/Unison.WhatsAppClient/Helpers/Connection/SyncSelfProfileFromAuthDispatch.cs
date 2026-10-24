// =============================================================================
// SyncSelfProfileFromAuthDispatch
//
// Body of SyncSelfProfileFromAuth. Auth / profile fields stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class SyncSelfProfileFromAuthDispatch
    {
        public static void Run(ISyncSelfProfileFromAuthHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.HasSelfMe)
            {
                return;
            }

            string persisted = host.NormalizePersistedSelfName();
            host.EnsureSelfPhonePersisted();

            if (!string.IsNullOrWhiteSpace(persisted))
            {
                host.CurrentUserName = persisted;
            }
            else if (host.IsOwnPhoneEchoLabel(host.CurrentUserName) ||
                     host.IsOwnPhoneEchoLabel(host.SelfMeNameForEchoCheck))
            {
                // A previous run seeded the name with the JID digits. Leave the display
                // name empty so the UI can fall back to MePhone until a real push name arrives.
                if (host.IsOwnPhoneEchoLabel(host.SelfMeNameForEchoCheck))
                {
                    host.ClearSelfMeName();
                    host.PersistAuthStateClearPhoneEchoName();
                }

                host.CurrentUserName = null;
            }

            // Restore cached URI only; do not clear an in-memory value if auth has none.
            if (string.IsNullOrWhiteSpace(host.CurrentUserAvatar) &&
                !string.IsNullOrWhiteSpace(host.SelfMeAvatarUrl))
            {
                host.RestoreSelfAvatar(host.SelfMeAvatarUrl);
            }
        }
    }
}
