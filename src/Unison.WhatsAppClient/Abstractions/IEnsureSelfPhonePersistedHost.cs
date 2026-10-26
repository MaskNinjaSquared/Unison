// =============================================================================
// IEnsureSelfPhonePersistedHost
//
// Side effects for EnsureSelfPhonePersistedDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IEnsureSelfPhonePersistedHost
    {
        bool HasSelfMe { get; }

        string SelfMeId { get; }

        string SelfMePhone { get; }

        void SetSelfMePhone(string phone);

        void NotifyCurrentUserPhoneChanged();

        void NotifySelfProfileChanged();

        void PersistAuthStateForSelfPhone();
    }
}
