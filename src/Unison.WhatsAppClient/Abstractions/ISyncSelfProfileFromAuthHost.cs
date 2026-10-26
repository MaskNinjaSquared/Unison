// =============================================================================
// ISyncSelfProfileFromAuthHost
//
// Side effects for SyncSelfProfileFromAuthDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ISyncSelfProfileFromAuthHost
    {
        bool HasSelfMe { get; }

        string NormalizePersistedSelfName();

        void EnsureSelfPhonePersisted();

        string CurrentUserName { get; set; }

        string SelfMeNameForEchoCheck { get; }

        bool IsOwnPhoneEchoLabel(string label);

        void ClearSelfMeName();

        void PersistAuthStateClearPhoneEchoName();

        string CurrentUserAvatar { get; }

        string SelfMeAvatarUrl { get; }

        void RestoreSelfAvatar(string avatarUrl);
    }
}
