// =============================================================================
// Tests for ChatListDeepLinkSyncUi — toast/tile open suppresses sync banner
// until the conversation finishes loading.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListDeepLinkSyncUiTests
    {
        [Fact]
        public void Deep_link_hold_suppresses_sync_banner()
        {
            Assert.True(ChatListDeepLinkSyncUi.ShouldSuppressSyncBanner(deepLinkHeld: true));
        }

        [Fact]
        public void After_release_sync_banner_may_show()
        {
            Assert.False(ChatListDeepLinkSyncUi.ShouldSuppressSyncBanner(deepLinkHeld: false));
        }

        [Fact]
        public void Cold_toast_prefers_loading_overlay()
        {
            Assert.True(ChatListDeepLinkSyncUi.ShouldPreferLoadingOverlay(
                deepLinkHeld: true,
                listAlreadyMaterialized: false));
        }

        [Fact]
        public void Warm_toast_does_not_cover_existing_list_with_loading()
        {
            Assert.False(ChatListDeepLinkSyncUi.ShouldPreferLoadingOverlay(
                deepLinkHeld: true,
                listAlreadyMaterialized: true));
        }

        [Fact]
        public void Without_hold_overlay_may_show_sync_progress()
        {
            Assert.False(ChatListDeepLinkSyncUi.ShouldPreferLoadingOverlay(
                deepLinkHeld: false,
                listAlreadyMaterialized: false));
        }
    }
}
