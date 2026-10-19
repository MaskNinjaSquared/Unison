// =============================================================================
// Tests for WebMessageParticipant.
//
// A message the phone resends (placeholder recovery) arrives as a WebMessageInfo
// and goes back through the live pump, so the sender has to be read off the
// envelope. MessageKey.participant is often unset there; WebMessageInfo.participant
// or an alt JID the generated schema does not declare may carry it instead.
// =============================================================================
using System.Text;
using Google.Protobuf;
using Proto;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class WebMessageParticipantTests
    {
        private const string Group = "120363000000000000@g.us";
        private const string Alice = "5511999990001@s.whatsapp.net";
        private const string Bob = "5511999990002@s.whatsapp.net";

        [Fact]
        public void The_key_participant_wins()
        {
            var info = new WebMessageInfo
            {
                Key = new MessageKey { RemoteJid = Group, Participant = Alice },
                Participant = Bob
            };

            Assert.Equal(Alice, WebMessageParticipant.Resolve(info));
        }

        [Fact]
        public void The_envelope_participant_fills_an_empty_key()
        {
            var info = new WebMessageInfo
            {
                Key = new MessageKey { RemoteJid = Group },
                Participant = Bob
            };

            Assert.Equal(Bob, WebMessageParticipant.Resolve(info));
        }

        [Fact]
        public void An_alt_jid_in_an_undeclared_key_field_is_the_last_resort()
        {
            var info = new WebMessageInfo { Key = KeyWithAltField(Group, "222@lid") };

            Assert.Equal("222@lid", WebMessageParticipant.Resolve(info));
        }

        [Fact]
        public void The_result_is_normalized()
        {
            var info = new WebMessageInfo
            {
                Key = new MessageKey { RemoteJid = Group, Participant = "5511999990001:7@s.whatsapp.net" }
            };

            Assert.Equal(
                JidHelper.Normalize("5511999990001:7@s.whatsapp.net"),
                WebMessageParticipant.Resolve(info));
        }

        [Theory]
        [InlineData(Group)]
        [InlineData("status@broadcast")]
        public void A_chat_address_is_never_the_participant(string chatAddress)
        {
            var info = new WebMessageInfo
            {
                Key = new MessageKey { RemoteJid = Group, Participant = chatAddress }
            };

            Assert.Null(WebMessageParticipant.Resolve(info));
        }

        [Fact]
        public void Nothing_to_read_is_nothing()
        {
            Assert.Null(WebMessageParticipant.Resolve(null));
            Assert.Null(WebMessageParticipant.Resolve(new WebMessageInfo()));
            Assert.Null(WebMessageParticipant.Resolve(
                new WebMessageInfo { Key = new MessageKey { RemoteJid = Group } }));
        }

        [Fact]
        public void A_direct_envelope_with_no_participant_answers_its_own_chat()
        {
            // The alt scan skips only field 4, so remoteJid (field 1) is read when it is a user.
            var info = new WebMessageInfo { Key = new MessageKey { RemoteJid = Alice } };

            Assert.Equal(Alice, WebMessageParticipant.Resolve(info));
        }

        private static MessageKey KeyWithAltField(string remoteJid, string alt)
        {
            byte[] declared = new MessageKey { RemoteJid = remoteJid }.ToByteArray();
            byte[] payload = Encoding.UTF8.GetBytes(alt);
            byte[] undeclared = new byte[2 + payload.Length];
            undeclared[0] = (5 << 3) | 2;
            undeclared[1] = (byte)payload.Length;
            payload.CopyTo(undeclared, 2);

            byte[] bytes = new byte[declared.Length + undeclared.Length];
            declared.CopyTo(bytes, 0);
            undeclared.CopyTo(bytes, declared.Length);
            return MessageKey.Parser.ParseFrom(bytes);
        }
    }
}
