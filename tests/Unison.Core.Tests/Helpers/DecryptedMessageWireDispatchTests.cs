using System;
using System.Collections.Generic;
using Proto;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class DecryptedMessageWireDispatchTests
    {
        [Fact]
        public void Observes_then_enqueues()
        {
            var host = new FakeHost();
            var message = new Message();
            var stamp = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            DecryptedMessageWireDispatch.Handle(
                host,
                "chat@s.whatsapp.net",
                "part@lid",
                "alt",
                "lid",
                "mid",
                message,
                stamp,
                isFromMe: false,
                pushName: "P",
                verifiedName: "V",
                senderLid: "slid",
                peerRecipientPn: "pn",
                peerRecipientLid: "lid2",
                recipientJid: "r@s.whatsapp.net",
                isOffline: true);

            Assert.Equal(
                new[] { "observe", "enqueue:mid|True" },
                host.Log);
            Assert.Same(message, host.EnqueuedMessage);
        }

        private sealed class FakeHost : IDecryptedMessageWireHost
        {
            public List<string> Log { get; } = new List<string>();

            public Message EnqueuedMessage { get; private set; }

            public void ObserveDecryptedDiagnostics() => Log.Add("observe");

            public void EnqueueDecryptedFromWire(
                string fromJid,
                string participant,
                string participantAlt,
                string addressingMode,
                string messageId,
                Message message,
                DateTime timestamp,
                bool isFromMe,
                string pushName,
                string verifiedName,
                string senderLid,
                string peerRecipientPn,
                string peerRecipientLid,
                string recipientJid,
                bool isOffline)
            {
                EnqueuedMessage = message;
                Log.Add("enqueue:" + messageId + "|" + isOffline);
            }
        }
    }
}
