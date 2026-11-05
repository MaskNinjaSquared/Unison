using Google.Protobuf;
using Unison.Baileys.Client;
using Unison.Socket.Utils;
using Xunit;

namespace Unison.Core.Tests.Messages
{
    public sealed class SenderKeyPaddingTests
    {
        private const string GroupJid = "120363000000000001@g.us";
        private const string SenderJid = "111111111111111:2@lid";

        [Theory]
        [InlineData(new byte[] { 0x0A, 0x03, 0x61, 0x62, 0x63, 0x08, 0x01 })]
        [InlineData(new byte[] { 0x0A, 0x02, 0x68, 0x69, 0x10, 0x10 })]
        [InlineData(new byte[] { 0x0A, 0x02, 0x68, 0x69, 0x18, 0x63 })]
        public void A_group_message_survives_the_single_unpad_of_the_decryptor(byte[] protobuf)
        {
            var sender = new SignalHandler(AuthState.Create());
            var receiver = new SignalHandler(AuthState.Create());

            SignalHandler.GroupEncryptResult sent = sender.EncryptGroupMessage(GroupJid, SenderJid, protobuf);
            receiver.ProcessSenderKeyDistribution(
                SenderJid,
                new global::Proto.Message.Types.SenderKeyDistributionMessage
                {
                    GroupId = GroupJid,
                    AxolotlSenderKeyDistributionMessage = ByteString.CopyFrom(sent.SenderKeyDistributionMessage)
                });

            byte[] plain = receiver.DecryptMessage(sent.Ciphertext, SenderJid, "skmsg", GroupJid);

            Assert.NotNull(plain);
            Assert.Equal(protobuf, WaPadding.UnpadRandomMax16(plain));
        }
    }
}
