using Unison.Voip.Engine.Relay;

namespace Unison.Core.Tests.Voip.Relay
{
    // Ports: meowcaller relay/relay_test.go (MIT)
    public sealed class RelayPacketClassifierTests
    {
        public static IEnumerable<object[]> FirstByteCases()
        {
            yield return new object[] { new byte[] { 0x00, 0x01 }, RelayPacketKind.Stun };
            yield return new object[] { new byte[] { 0x00, 0x03 }, RelayPacketKind.Stun };
            yield return new object[] { new byte[] { 0x80, 0xc8 }, RelayPacketKind.Rtcp };
            yield return new object[] { new byte[] { 0x81, 0xc8 }, RelayPacketKind.Rtcp };
            yield return new object[] { new byte[] { 0x91, 0xce }, RelayPacketKind.Rtcp };
            yield return new object[] { new byte[] { 0x90, 0x78 }, RelayPacketKind.Rtp };
            yield return new object[] { new byte[] { 0x90, 0xe1 }, RelayPacketKind.Rtp };
            yield return new object[] { new byte[] { 0xff, 0xff }, RelayPacketKind.Other };
            yield return new object[] { new byte[] { 0x00 }, RelayPacketKind.Other };
        }

        [Theory]
        [MemberData(nameof(FirstByteCases))]
        public void Classify_first_byte(byte[] data, RelayPacketKind want)
        {
            Assert.Equal(want, RelayPacketClassifier.ClassifyRelayPacket(data));
        }

        [Fact]
        public void Classify_null_is_other()
        {
            Assert.Equal(RelayPacketKind.Other, RelayPacketClassifier.ClassifyRelayPacket(null!));
        }
    }
}
