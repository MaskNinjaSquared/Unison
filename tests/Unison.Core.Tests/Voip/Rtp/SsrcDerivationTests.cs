using Unison.Voip.Engine.Rtp;
using Xunit;

namespace Unison.Core.Tests.Voip.Rtp
{
    public sealed class SsrcDerivationTests
    {
        [Fact]
        public void Ssrc_matches_kat()
        {
            string callId = RtpKats.Str("inputs", "callId");
            string peerLid = RtpKats.Str("inputs", "peerLid");

            Assert.Equal(RtpKats.U32("voip_crypto", "ssrc_slot0"), SsrcDerivation.DeriveWasmParticipantSsrc(callId, peerLid, 0));
            Assert.Equal(RtpKats.U32("voip_crypto", "ssrc_slot1"), SsrcDerivation.DeriveWasmParticipantSsrc(callId, peerLid, 1));
        }

        [Fact]
        public void Relay_stream_ssrcs_follow_slot_word_order()
        {
            string callId = RtpKats.Str("inputs", "callId");
            string peerLid = RtpKats.Str("inputs", "peerLid");

            uint[] all = SsrcDerivation.DeriveWasmRelayStreamSsrcs(callId, peerLid);
            uint[] slots = SsrcDerivation.WasmRelayStreamSlotWords;

            Assert.Equal(9, all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                Assert.Equal(SsrcDerivation.DeriveWasmParticipantSsrc(callId, peerLid, slots[i]), all[i]);
            }
            Assert.Equal(RtpKats.U32("voip_crypto", "ssrc_slot0"), all[0]);
            Assert.Equal(RtpKats.U32("voip_crypto", "ssrc_slot1"), all[1]);
        }

        [Theory]
        [InlineData("12345@lid", "12345:0@lid")]
        [InlineData("12345:6@lid", "12345:6@lid")]
        [InlineData("12345@s.whatsapp.net", "12345@s.whatsapp.net")]
        public void Format_participant_id_rules(string input, string expected)
        {
            Assert.Equal(expected, SsrcDerivation.FormatE2ESrtpParticipantID(input));
        }

        [Fact]
        public void E2e_participant_id_variants_are_deduplicated()
        {
            Assert.Equal(
                new[] { "12345:6@lid", "12345:0@lid", "12345@lid" },
                SsrcDerivation.E2EParticipantIDVariants("12345:6@lid"));
        }

        [Fact]
        public void Captured_app_data_ssrc_uses_slot_six()
        {
            uint got = SsrcDerivation.DeriveWasmParticipantSsrc(
                "A8FE932D75AEE1DEDFC3A877E7238B88",
                "242653052539031:0@lid",
                SsrcDerivation.AppDataSlotWord);

            Assert.Equal(2135790987u, got);
        }
    }
}
