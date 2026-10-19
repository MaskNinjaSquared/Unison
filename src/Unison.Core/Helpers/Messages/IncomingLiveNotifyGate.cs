// =============================================================================
// IncomingLiveNotifyGate
//
// Whether the live-incoming path should call the notification surface at all.
// From-me echoes never announce here (toast, badge, tile bump). Mute and
// on-screen suppress stay on INotificationService for the announce path.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class IncomingLiveNotifyGate
    {
        public static bool ShouldAnnounce(bool isFromMe) => !isFromMe;
    }
}
