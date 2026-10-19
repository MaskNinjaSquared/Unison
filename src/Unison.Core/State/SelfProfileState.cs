// =============================================================================
// SelfProfileState
//
// The logged-in account's name and picture as the app shows them. The profile
// façade writes here after it fetches the picture; the client writes the name
// when a push name or the credentials arrive. Both read the same snapshot.
//
// The account itself (id, LID, phone, credentials name, the picture saved with
// the credentials) still belongs to the client's auth state; it binds readers
// once, like JidAliasTable.BindSelf, plus the phone-echo rule and how a new
// picture is persisted.
// =============================================================================
using System;
using System.Diagnostics;
using Unison.Core.Models;

namespace Unison.Core.State
{
    /// <summary>What the client's auth state knows about the account right now.</summary>
    public sealed class SelfProfileAccount
    {
        public string Id;
        public string Lid;

        /// <summary>The credentials name, already cleaned of self markers; null when it is only digits.</summary>
        public string CredentialsName;

        public string Phone;

        /// <summary>The picture saved with the credentials.</summary>
        public string AvatarUrl;
    }

    public sealed class SelfProfileState
    {
        private Func<SelfProfileAccount> _account = () => null;
        private Func<string, bool> _isOwnPhoneEcho = label => false;
        private Action<string> _persistAvatar = url => { };
        private string _name;
        private string _avatarUrl;

        /// <summary>Raised on the writer's thread whenever the name, picture or account changes.</summary>
        public event EventHandler Changed;

        public void Bind(
            Func<SelfProfileAccount> account,
            Func<string, bool> isOwnPhoneEcho,
            Action<string> persistAvatar)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _isOwnPhoneEcho = isOwnPhoneEcho ?? throw new ArgumentNullException(nameof(isOwnPhoneEcho));
            _persistAvatar = persistAvatar ?? throw new ArgumentNullException(nameof(persistAvatar));
        }

        /// <summary>The name chosen for the account. Never the account's own phone digits.</summary>
        public string Name
        {
            get => _name;
            set
            {
                string next = _isOwnPhoneEcho(value) ? null : value;
                if (string.Equals(_name, next, StringComparison.Ordinal))
                {
                    return;
                }

                _name = next;
                NotifyChanged();
            }
        }

        /// <summary>The account's picture; setting it also persists it with the credentials.</summary>
        public string AvatarUrl
        {
            get => _avatarUrl;
            set
            {
                if (string.Equals(_avatarUrl, value, StringComparison.Ordinal))
                {
                    return;
                }

                _avatarUrl = value;
                _persistAvatar(value);
                NotifyChanged();
            }
        }

        /// <summary>Puts back the picture read from the credentials, without saving it again.</summary>
        public void RestoreAvatar(string avatarUrl)
        {
            if (string.Equals(_avatarUrl, avatarUrl, StringComparison.Ordinal))
            {
                return;
            }

            _avatarUrl = avatarUrl;
            NotifyChanged();
        }

        public Profile Snapshot()
        {
            SelfProfileAccount account = _account() ?? new SelfProfileAccount();
            return new Profile
            {
                Id = account.Id,
                Lid = account.Lid,
                Name = !string.IsNullOrWhiteSpace(_name) ? _name : account.CredentialsName,
                Phone = account.Phone,
                AvatarUrl = !string.IsNullOrWhiteSpace(_avatarUrl) ? _avatarUrl : account.AvatarUrl
            };
        }

        /// <summary>For changes on the account side, such as the phone being filled in.</summary>
        public void NotifyChanged()
        {
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SelfProfileState] Listener failed: " + ex.Message);
            }
        }
    }
}
