// =============================================================================
// CallDirectionGlyphs
//
// Segoe Fluent: IncomingCall E77E, CallForward (feita) E7F2; missed uses inbound.
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.Helpers.Calls
{
    public static class CallDirectionGlyphs
    {
        public const string Outgoing = "\uE7F2";

        public const string Incoming = "\uE77E";

        public const string Missed = "\uE77E";

        public static string For(CallDirection direction)
        {
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
