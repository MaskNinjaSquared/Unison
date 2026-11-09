// =============================================================================
// MobileChatListAvatarGate
//
// Mobile list scroll stays offline (no IQ per realized row); desktop keeps the
// row-realize refresh path. Opening another chat revalidates on every device:
// one IQ per open, served past the profile-picture cache and written back.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class MobileChatListAvatarGate
    {
        public static bool AllowRefreshOnRowRealized(bool isMobile) => !isMobile;

        public static bool AllowRefreshOnChatOpen(bool isMobile, bool chatChanged, bool hasChat) =>
            chatChanged && hasChat;
    }
}
