// =============================================================================
// CallDirectionGlyphs
//
// Segoe Fluent: IncomingCall E77E, CallForward (feita) E7F2; missed uses inbound.
// Video calls use Video (E714) instead of the voice direction glyph.
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.Helpers.Calls
{
    public static class CallDirectionGlyphs
    {
        public const string Outgoing = "\uE7F2";

        public const string Incoming = "\uE77E";

        public const string Missed = "\uE77E";

        /// <summary>Segoe Video — used for video call chrome in bubbles and call log.</summary>
        public const string Video = "\uE714";

        public static string For(CallDirection direction, bool isVideo = false)
        {
            if (isVideo)
            {
                return Video;
            }

            switch (direction)
            {
                case CallDirection.Outgoing:
                    return Outgoing;
                case CallDirection.Missed:
                    return Missed;
                default:
                    return Incoming;
            }
        }
    }
}
