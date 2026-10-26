using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class LinkedAccountGateTests
    {
        [Fact]
        public void Linked_when_registered_me_has_id()
        {
            Assert.True(
                LinkedAccountGate.HasLinkedAccount(true, true, "5511999999999@s.whatsapp.net"));
        }

        [Theory]
        [InlineData(false, true, "x")]
        [InlineData(true, false, "x")]
        [InlineData(true, true, "")]
        [InlineData(true, true, "  ")]
        public void Not_linked_without_full_identity(bool registered, bool mePresent, string meId)
        {
            Assert.False(LinkedAccountGate.HasLinkedAccount(registered, mePresent, meId));
        }

        [Fact]
        public void Not_linked_when_me_id_is_null()
        {
            Assert.False(LinkedAccountGate.HasLinkedAccount(true, true, null));
        }

        [Fact]
        public void Registered_with_identity_requires_auth_me_and_flag()
        {
            Assert.True(LinkedAccountGate.IsRegisteredWithIdentity(true, true, true));
            Assert.False(LinkedAccountGate.IsRegisteredWithIdentity(false, true, true));
            Assert.False(LinkedAccountGate.IsRegisteredWithIdentity(true, false, true));
            Assert.False(LinkedAccountGate.IsRegisteredWithIdentity(true, true, false));
        }
    }
}
