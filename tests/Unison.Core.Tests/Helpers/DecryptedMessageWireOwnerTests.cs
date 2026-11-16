using Proto;
using Unison.WhatsAppClient.Helpers;
using Unison.WhatsAppClient.Messages;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class DecryptedMessageWireOwnerTests
    {
        [Fact]
        public void Handle_observes_then_enqueues_the_mapped_pump_request()
        {
            bool observed = false;
            IncomingDecryptedNotifyRequest seen = null;
            var owner = new DecryptedMessageWireOwner(
                () => observed = true,
                request => seen = request);

            owner.Handle(
                "chat@s.whatsapp.net",
                "p@s.whatsapp.net",
                "p@lid",
                "lid",
                "MID1",
                new Message(),
                System.DateTime.UtcNow,
                true,
                "push",
                "verified",
                "sender@lid",
                "peer@s.whatsapp.net",
                "peer@lid",
                "me@s.whatsapp.net",
                true);

            Assert.True(observed);
            Assert.NotNull(seen);
            Assert.Equal("MID1", seen.MessageId);
            Assert.Equal("chat@s.whatsapp.net", seen.FromJid);
            Assert.Equal("p@s.whatsapp.net", seen.ParticipantJid);
            Assert.Equal("p@lid", seen.ParticipantAltJid);
            Assert.Equal("me@s.whatsapp.net", seen.RecipientJid);
            Assert.Equal("peer@s.whatsapp.net", seen.PeerRecipientPn);
            Assert.Equal("peer@lid", seen.PeerRecipientLid);
            Assert.Equal("sender@lid", seen.SenderLid);
            Assert.Equal("push", seen.PushName);
            Assert.Equal("verified", seen.VerifiedName);
            Assert.True(seen.IsOffline);
            Assert.True(seen.IsFromMe);
        }
    }
}
