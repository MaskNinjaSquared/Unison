using System;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class IncomingDecryptedNotifyMapTests
    {
        [Fact]
        public void FromPump_copies_scalars_and_leaves_self_display_blank()
        {
            var stamp = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            var request = IncomingDecryptedNotifyMap.FromPump(
                message: null,
                messageId: "mid",
                fromJid: "a@s.whatsapp.net",
                recipientJid: "b@s.whatsapp.net",
                peerRecipientPn: "pn",
                peerRecipientLid: "lid",
                senderLid: "sender",
                participantJid: "p",
                participantAltJid: "alt",
                verifiedName: "v",
                pushName: "push",
                timestamp: stamp,
                isOffline: true,
                isFromMe: false);

            Assert.Equal("mid", request.MessageId);
            Assert.Equal("a@s.whatsapp.net", request.FromJid);
            Assert.Equal("b@s.whatsapp.net", request.RecipientJid);
            Assert.Equal("pn", request.PeerRecipientPn);
            Assert.Equal("lid", request.PeerRecipientLid);
            Assert.Equal("sender", request.SenderLid);
            Assert.Equal("p", request.ParticipantJid);
            Assert.Equal("alt", request.ParticipantAltJid);
            Assert.Equal("v", request.VerifiedName);
            Assert.Equal("push", request.PushName);
            Assert.Equal(stamp, request.Timestamp);
            Assert.True(request.IsOffline);
            Assert.False(request.IsFromMe);
            Assert.Null(request.SelfDisplayName);
        }
    }
}
