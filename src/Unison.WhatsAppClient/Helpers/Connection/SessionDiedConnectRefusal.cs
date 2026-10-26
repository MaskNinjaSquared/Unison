// =============================================================================
// SessionDiedConnectRefusal
//
// ConnectAsync must stand down when the fatal latch is set and the account is
// still registered (or auth was wiped). Unregistered QR after wipe may proceed.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public static class SessionDiedConnectRefusal
    {
        /// <summary>
        /// True when ConnectAsync must refuse: fatal latch set and either auth is
        /// gone or the companion is still registered.
        /// </summary>
        public static bool ShouldRefuse(
            bool fatalSessionEnded,
            bool authIsNull,
            bool authRegistered)
        {
            if (!fatalSessionEnded)
            {
                return false;
            }

            return authIsNull || authRegistered;
        }
    }
}
