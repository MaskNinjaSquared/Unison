using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FirstSyncUiAvatarPolicyTests
    {
        [Fact]
        public void Network_avatars_wait_while_list_hold_is_up()
        {
            Assert.True(FirstSyncUiAvatarPolicy.ShouldDeferNetworkAvatarFetch(
                firstSyncListHold: true,
                initialSyncActive: false));
        }

        [Fact]
        public void Network_avatars_wait_while_initial_sync_safe_mode_is_active()
        {
            Assert.True(FirstSyncUiAvatarPolicy.ShouldDeferNetworkAvatarFetch(
                firstSyncListHold: false,
                initialSyncActive: true));
        }

        [Fact]
        public void After_unlock_network_avatars_may_run()
        {
            Assert.False(FirstSyncUiAvatarPolicy.ShouldDeferNetworkAvatarFetch(
                firstSyncListHold: false,
                initialSyncActive: false));
        }
    }
}
