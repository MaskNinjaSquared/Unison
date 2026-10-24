// =============================================================================
// EnsureSelfPhonePersistedDispatch
//
// Body of EnsureSelfPhonePersisted. Auth Me / profile notify stay on the host.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class EnsureSelfPhonePersistedDispatch
    {
        public static void Run(IEnsureSelfPhonePersistedHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.HasSelfMe)
            {
                return;
            }

            string phone = JidHelper.TryPhoneFromJid(host.SelfMeId);
            if (string.IsNullOrWhiteSpace(phone))
            {
                return;
            }

            if (string.Equals(host.SelfMePhone, phone, StringComparison.Ordinal))
            {
                return;
            }

            host.SetSelfMePhone(phone);
            host.NotifyCurrentUserPhoneChanged();
            host.NotifySelfProfileChanged();
            host.PersistAuthStateForSelfPhone();
        }
    }
}
