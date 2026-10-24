using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class EnsureSelfPhonePersistedDispatchTests
    {
        [Fact]
        public void No_me_is_noop()
        {
            var host = new FakeHost { HasSelfMe = false };
            EnsureSelfPhonePersistedDispatch.Run(host);
            Assert.False(host.Updated);
        }

        [Fact]
        public void Same_phone_skips()
        {
            var host = new FakeHost
            {
                HasSelfMe = true,
                SelfMeId = "5511999887766@s.whatsapp.net",
                SelfMePhone = "5511999887766"
            };
            EnsureSelfPhonePersistedDispatch.Run(host);
            Assert.False(host.Updated);
        }

        [Fact]
        public void Derives_phone_from_jid_and_persists()
        {
            var host = new FakeHost
            {
                HasSelfMe = true,
                SelfMeId = "5511999887766@s.whatsapp.net",
                SelfMePhone = null
            };
            EnsureSelfPhonePersistedDispatch.Run(host);
            Assert.True(host.Updated);
            Assert.Equal("5511999887766", host.SetPhone);
            Assert.True(host.NotifiedPhone);
            Assert.True(host.NotifiedProfile);
            Assert.True(host.Persisted);
        }

        private sealed class FakeHost : IEnsureSelfPhonePersistedHost
        {
            public bool HasSelfMe { get; set; }
            public string SelfMeId { get; set; }
            public string SelfMePhone { get; set; }
            public bool Updated;
            public string SetPhone;
            public bool NotifiedPhone;
            public bool NotifiedProfile;
            public bool Persisted;

            public void SetSelfMePhone(string phone)
            {
                Updated = true;
                SetPhone = phone;
            }

            public void NotifyCurrentUserPhoneChanged() => NotifiedPhone = true;

            public void NotifySelfProfileChanged() => NotifiedProfile = true;

            public void PersistAuthStateForSelfPhone() => Persisted = true;
        }
    }
}
