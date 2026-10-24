using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class SyncSelfProfileFromAuthDispatchTests
    {
        [Fact]
        public void No_me_is_noop()
        {
            var host = new FakeHost { HasSelfMe = false };
            SyncSelfProfileFromAuthDispatch.Run(host);
            Assert.False(host.PhoneEnsured);
        }

        [Fact]
        public void Persisted_name_wins()
        {
            var host = new FakeHost
            {
                HasSelfMe = true,
                Persisted = "Alice",
                CurrentUserName = "old"
            };
            SyncSelfProfileFromAuthDispatch.Run(host);
            Assert.Equal("Alice", host.CurrentUserName);
            Assert.True(host.PhoneEnsured);
        }

        [Fact]
        public void Phone_echo_clears_name()
        {
            var host = new FakeHost
            {
                HasSelfMe = true,
                Persisted = null,
                CurrentUserName = "5511999",
                MeName = "5511999",
                Echo = true
            };
            SyncSelfProfileFromAuthDispatch.Run(host);
            Assert.Null(host.CurrentUserName);
            Assert.True(host.MeNameCleared);
            Assert.True(host.AuthPersisted);
        }

        private sealed class FakeHost : ISyncSelfProfileFromAuthHost
        {
            public bool HasSelfMe { get; set; }
            public string Persisted;
            public string MeName;
            public bool Echo;
            public bool PhoneEnsured;
            public bool MeNameCleared;
            public bool AuthPersisted;
            public string CurrentUserName { get; set; }
            public string CurrentUserAvatar { get; set; }
            public string SelfMeAvatarUrl { get; set; }

            public string SelfMeNameForEchoCheck => MeName;

            public string NormalizePersistedSelfName() => Persisted;

            public void EnsureSelfPhonePersisted() => PhoneEnsured = true;

            public bool IsOwnPhoneEchoLabel(string label) => Echo && !string.IsNullOrEmpty(label);

            public void ClearSelfMeName() => MeNameCleared = true;

            public void PersistAuthStateClearPhoneEchoName() => AuthPersisted = true;

            public void RestoreSelfAvatar(string avatarUrl)
            {
            }
        }
    }
}
