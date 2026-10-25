// =============================================================================
// MobileChatListAvatarGate
//
// Mobile: first pictures come from the post-history avatar batch + disk hydrate.
// List scroll must not hit the wire. Opening a chat revalidates once; Apply
// skips the CDN download when the picture id/url is unchanged.
// Desktop keeps the existing row-realize refresh path.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class MobileChatListAvatarGate
    {
        public static bool AllowRefreshOnRowRealized(bool isMobile) => !isMobile;

        public static bool AllowRefreshOnChatOpen(bool isMobile, bool chatChanged, bool hasChat) =>
            isMobile && chatChanged && hasChat;
    }
}
