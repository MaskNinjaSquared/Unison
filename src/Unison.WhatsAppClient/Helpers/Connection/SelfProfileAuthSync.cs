// =============================================================================
// SelfProfileAuthSync
//
// Owns ISyncSelfProfileFromAuthHost + IEnsureSelfPhonePersistedHost +
// IPersistSelfAvatarUrlHost. Auth mutation / persist stay as ctor ports.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class SelfProfileAuthSync :
        ISyncSelfProfileFromAuthHost,
        IEnsureSelfPhonePersistedHost,
        IPersistSelfAvatarUrlHost
    {
        private readonly SelfProfileState _selfProfile;
        private readonly Func<bool> _hasSelfMe;
        private readonly Func<string> _normalizePersistedSelfName;
        private readonly Func<string> _getCurrentUserName;
        private readonly Action<string> _setCurrentUserName;
        private readonly Func<string> _selfMeName;
        private readonly Func<string, bool> _isOwnPhoneEchoLabel;
        private readonly Action _clearSelfMeName;
        private readonly Action _persistClearPhoneEcho;
        private readonly Func<string> _getCurrentUserAvatar;
        private readonly Func<string> _selfMeAvatarUrl;
        private readonly Action<string> _restoreSelfAvatar;
        private readonly Func<string> _selfMeId;
        private readonly Func<string> _selfMePhone;
        private readonly Action<string> _setSelfMePhone;
        private readonly Action _notifyPhoneChanged;
        private readonly Action _notifySelfProfileChanged;
        private readonly Action _persistSelfPhone;
        private readonly Action<string> _setSelfMeAvatarUrl;
        private readonly Func<bool> _canPersistAuth;
        private readonly Func<Task> _saveAuthStateAsync;

        public SelfProfileAuthSync(
            SelfProfileState selfProfile,
            Func<bool> hasSelfMe,
            Func<string> normalizePersistedSelfName,
            Func<string> getCurrentUserName,
            Action<string> setCurrentUserName,
            Func<string> selfMeName,
            Func<string, bool> isOwnPhoneEchoLabel,
            Action clearSelfMeName,
            Action persistClearPhoneEcho,
            Func<string> getCurrentUserAvatar,
            Func<string> selfMeAvatarUrl,
            Action<string> restoreSelfAvatar,
            Func<string> selfMeId,
            Func<string> selfMePhone,
            Action<string> setSelfMePhone,
            Action notifyPhoneChanged,
            Action notifySelfProfileChanged,
            Action persistSelfPhone,
            Action<string> setSelfMeAvatarUrl,
            Func<bool> canPersistAuth,
            Func<Task> saveAuthStateAsync)
        {
            _selfProfile = selfProfile ?? throw new ArgumentNullException(nameof(selfProfile));
            _hasSelfMe = hasSelfMe ?? throw new ArgumentNullException(nameof(hasSelfMe));
            _normalizePersistedSelfName = normalizePersistedSelfName
                ?? throw new ArgumentNullException(nameof(normalizePersistedSelfName));
            _getCurrentUserName = getCurrentUserName
                ?? throw new ArgumentNullException(nameof(getCurrentUserName));
            _setCurrentUserName = setCurrentUserName
                ?? throw new ArgumentNullException(nameof(setCurrentUserName));
            _selfMeName = selfMeName ?? throw new ArgumentNullException(nameof(selfMeName));
            _isOwnPhoneEchoLabel = isOwnPhoneEchoLabel
                ?? throw new ArgumentNullException(nameof(isOwnPhoneEchoLabel));
            _clearSelfMeName = clearSelfMeName
                ?? throw new ArgumentNullException(nameof(clearSelfMeName));
            _persistClearPhoneEcho = persistClearPhoneEcho
                ?? throw new ArgumentNullException(nameof(persistClearPhoneEcho));
            _getCurrentUserAvatar = getCurrentUserAvatar
                ?? throw new ArgumentNullException(nameof(getCurrentUserAvatar));
            _selfMeAvatarUrl = selfMeAvatarUrl
                ?? throw new ArgumentNullException(nameof(selfMeAvatarUrl));
            _restoreSelfAvatar = restoreSelfAvatar
                ?? throw new ArgumentNullException(nameof(restoreSelfAvatar));
            _selfMeId = selfMeId ?? throw new ArgumentNullException(nameof(selfMeId));
            _selfMePhone = selfMePhone ?? throw new ArgumentNullException(nameof(selfMePhone));
            _setSelfMePhone = setSelfMePhone
                ?? throw new ArgumentNullException(nameof(setSelfMePhone));
            _notifyPhoneChanged = notifyPhoneChanged
                ?? throw new ArgumentNullException(nameof(notifyPhoneChanged));
            _notifySelfProfileChanged = notifySelfProfileChanged
                ?? throw new ArgumentNullException(nameof(notifySelfProfileChanged));
            _persistSelfPhone = persistSelfPhone
                ?? throw new ArgumentNullException(nameof(persistSelfPhone));
            _setSelfMeAvatarUrl = setSelfMeAvatarUrl
                ?? throw new ArgumentNullException(nameof(setSelfMeAvatarUrl));
            _canPersistAuth = canPersistAuth
                ?? throw new ArgumentNullException(nameof(canPersistAuth));
            _saveAuthStateAsync = saveAuthStateAsync
                ?? throw new ArgumentNullException(nameof(saveAuthStateAsync));
        }

        public void SyncFromAuth() => SyncSelfProfileFromAuthDispatch.Run(this);

        public void EnsureSelfPhonePersisted() =>
            EnsureSelfPhonePersistedDispatch.Run(this);

        public void PersistAvatarUrl(string avatarUrl) =>
            PersistSelfAvatarUrlDispatch.Run(this, avatarUrl);

        bool ISyncSelfProfileFromAuthHost.HasSelfMe => _hasSelfMe();

        string ISyncSelfProfileFromAuthHost.NormalizePersistedSelfName() =>
            _normalizePersistedSelfName();

        void ISyncSelfProfileFromAuthHost.EnsureSelfPhonePersisted() =>
            EnsureSelfPhonePersisted();

        string ISyncSelfProfileFromAuthHost.CurrentUserName
        {
            get => _getCurrentUserName();
            set => _setCurrentUserName(value);
        }

        string ISyncSelfProfileFromAuthHost.SelfMeNameForEchoCheck => _selfMeName();

        bool ISyncSelfProfileFromAuthHost.IsOwnPhoneEchoLabel(string label) =>
            _isOwnPhoneEchoLabel(label);

        void ISyncSelfProfileFromAuthHost.ClearSelfMeName() => _clearSelfMeName();

        void ISyncSelfProfileFromAuthHost.PersistAuthStateClearPhoneEchoName() =>
            _persistClearPhoneEcho();

        string ISyncSelfProfileFromAuthHost.CurrentUserAvatar => _getCurrentUserAvatar();

        string ISyncSelfProfileFromAuthHost.SelfMeAvatarUrl => _selfMeAvatarUrl();

        void ISyncSelfProfileFromAuthHost.RestoreSelfAvatar(string avatarUrl) =>
            _restoreSelfAvatar(avatarUrl);

        bool IEnsureSelfPhonePersistedHost.HasSelfMe => _hasSelfMe();

        string IEnsureSelfPhonePersistedHost.SelfMeId => _selfMeId();

        string IEnsureSelfPhonePersistedHost.SelfMePhone => _selfMePhone();

        void IEnsureSelfPhonePersistedHost.SetSelfMePhone(string phone) =>
            _setSelfMePhone(phone);

        void IEnsureSelfPhonePersistedHost.NotifyCurrentUserPhoneChanged() =>
            _notifyPhoneChanged();

        void IEnsureSelfPhonePersistedHost.NotifySelfProfileChanged() =>
            _notifySelfProfileChanged();

        void IEnsureSelfPhonePersistedHost.PersistAuthStateForSelfPhone() =>
            _persistSelfPhone();

        bool IPersistSelfAvatarUrlHost.HasSelfMe => _hasSelfMe();

        string IPersistSelfAvatarUrlHost.SelfMeAvatarUrl => _selfMeAvatarUrl();

        void IPersistSelfAvatarUrlHost.SetSelfMeAvatarUrl(string avatarUrl) =>
            _setSelfMeAvatarUrl(avatarUrl);

        bool IPersistSelfAvatarUrlHost.CanPersistAuth => _canPersistAuth();

        Task IPersistSelfAvatarUrlHost.SaveAuthStateAsync() => _saveAuthStateAsync();

        void IPersistSelfAvatarUrlHost.LogSelfAvatarPersistFailed(string message) =>
            Debug.WriteLine(
                "[SelfProfileAuthSync] Failed to persist self avatar URI: " + message);
    }
}
