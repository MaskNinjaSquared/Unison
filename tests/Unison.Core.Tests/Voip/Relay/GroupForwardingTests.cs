using Unison.Voip.Engine.Relay;

namespace Unison.Core.Tests.Voip.Relay
{
    // Ports: meowcaller relay/group_forwarding_test.go (MIT)
    public sealed class GroupForwardingTests
    {
        [Theory]
        [InlineData("subtype_2_video", "0902336e0100018890e1001300120c96772e6aa9", "90e1001300120c96772e6aa9")]
        [InlineData("subtype_4_video", "09047eb5410001000000001090e100010013fbf08f1df407", "90e100010013fbf08f1df407")]
        [InlineData("subtype_7_audio", "0907338f2900020a00c801e000000000000090780001000331808cd481d5", "90780001000331808cd481d5")]
        public void Unwrap_capture_vectors(string name, string packetHex, string innerHex)
        {
            bool wrapped;
            bool valid;
            byte[] got = GroupForwarding.UnwrapGroupForwardingPacket(Convert.FromHexString(packetHex), out wrapped, out valid);

            Assert.True(wrapped, name);
            Assert.True(valid, name);
            Assert.Equal(Convert.FromHexString(innerHex), got);
            Assert.Equal(RelayPacketKind.Rtp, RelayPacketClassifier.ClassifyRelayPacket(got));
        }

        [Fact]
        public void Unwrap_passes_through_ordinary_rtp()
        {
            byte[] packet = { 0x90, 0xe1, 0, 1, 0, 0, 0, 1, 0, 0, 0, 2 };
            bool wrapped;
            bool valid;
            byte[] got = GroupForwarding.UnwrapGroupForwardingPacket(packet, out wrapped, out valid);

            Assert.False(wrapped);
            Assert.True(valid);
            Assert.Equal(packet, got);
        }

        public static IEnumerable<object[]> MalformedCases()
        {
            yield return new object[] { new byte[] { 0x09 } };
            yield return new object[] { new byte[] { 0x09, 0xff } };
            yield return new object[] { new byte[] { 0x09, 0x02, 0, 0, 0, 0, 0, 0 } };
            yield return new object[] { new byte[] { 0x09, 0x02, 0, 0, 0, 0, 0, 0, 0x10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } };
        }

        [Theory]
        [MemberData(nameof(MalformedCases))]
        public void Unwrap_rejects_malformed(byte[] packet)
        {
            bool wrapped;
            bool valid;
            byte[] got = GroupForwarding.UnwrapGroupForwardingPacket(packet, out wrapped, out valid);

            Assert.True(wrapped);
            Assert.False(valid);
            Assert.Null(got);
        }
    }
}
