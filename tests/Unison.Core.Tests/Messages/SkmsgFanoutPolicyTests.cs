// =============================================================================
// status@broadcast vs group fan-out — rc14 relayMessage.
// =============================================================================
using Unison.Socket.Messages;
using Xunit;

namespace Unison.Core.Tests.Messages
{
    public sealed class SkmsgFanoutPolicyTests
    {
        [Fact]
        public void Status_sends_only_to_devices_usync_returned()
        {
            Assert.False(SkmsgFanoutPolicy.FillsMissingPrimaryDevices(isStatus: true));
        }

        [Fact]
        public void Group_still_fills_in_missing_primaries()
        {
            Assert.True(SkmsgFanoutPolicy.FillsMissingPrimaryDevices(isStatus: false));
        }

        [Fact]
        public void Status_redistributes_the_sender_key_every_time()
        {
            Assert.False(SkmsgFanoutPolicy.UsesSenderKeyMemory(isStatus: true));
        }

        [Fact]
        public void Group_skips_devices_that_already_hold_the_key()
        {
            Assert.True(SkmsgFanoutPolicy.UsesSenderKeyMemory(isStatus: false));
        }
    }
}
