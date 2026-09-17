using System.Collections.Generic;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class IncomingEnvelopeAliasHintsTests
    {
        [Fact]
        public void Sender_lid_pairs_with_a_phone_from()
        {
            IReadOnlyList<IncomingAliasHint> hints = IncomingEnvelopeAliasHints.Collect(
                senderLid: "123456789012345@lid",
                fromJid: "5511999999999@s.whatsapp.net",
                peerRecipientPn: null,
                peerRecipientLid: null,
                recipientJid: null,
                participant: null,
                participantAlt: null);

            Assert.Single(hints);
            Assert.Equal("sender_lid", hints[0].Source);
            Assert.Equal("123456789012345@lid", hints[0].LidJid);
            Assert.Equal("5511999999999@s.whatsapp.net", hints[0].PnJid);
        }

        [Fact]
        public void Group_participant_alt_puts_the_lid_on_the_lid_side()
        {
            IReadOnlyList<IncomingAliasHint> hints = IncomingEnvelopeAliasHints.Collect(
                senderLid: null,
                fromJid: "120363000000000000@g.us",
                peerRecipientPn: null,
                peerRecipientLid: null,
                recipientJid: null,
                participant: "5511999999999@s.whatsapp.net",
                participantAlt: "123456789012345@lid");

            Assert.Single(hints);
            Assert.Equal("group-participant-alt", hints[0].Source);
            Assert.Equal("123456789012345@lid", hints[0].LidJid);
            Assert.Equal("5511999999999@s.whatsapp.net", hints[0].PnJid);
        }

        [Fact]
        public void A_lid_from_without_peer_pn_yields_nothing()
        {
            Assert.Empty(IncomingEnvelopeAliasHints.Collect(
                senderLid: null,
                fromJid: "123456789012345@lid",
                peerRecipientPn: null,
                peerRecipientLid: null,
                recipientJid: null,
                participant: null,
                participantAlt: null));
        }
    }
}
