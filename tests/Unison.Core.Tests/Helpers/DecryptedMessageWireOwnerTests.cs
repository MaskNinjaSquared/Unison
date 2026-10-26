using Proto;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class DecryptedMessageWireOwnerTests
    {
        [Fact]
        public void Handle_observes_then_enqueues()
        {
            bool observed = false;
            string seenId = null;
            var owner = new DecryptedMessageWireOwner(
                () => observed = true,
                (from, participant, alt, mode, id, message, ts, fromMe, push, verified,
                    senderLid, peerPn, peerLid, recipient, offline) =>
                {
                    seenId = id;
                });

            owner.Handle(
                "chat@s.whatsapp.net",
                null,
                null,
                null,
                "MID1",
                new Message(),
                System.DateTime.UtcNow,
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                false);

            Assert.True(observed);
            Assert.Equal("MID1", seenId);
        }
    }
}
