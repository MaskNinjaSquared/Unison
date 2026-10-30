// =============================================================================
// MessageBubbleReplySwipe
//
// Decides when a horizontal message-bubble gesture means "reply". The UWP views
// own manipulation events; keeping the threshold here makes every shell use the
// same decision without bringing WinRT types into Core.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class MessageBubbleReplySwipe
    {
        /// <summary>
        /// A deliberate thumb drag on Mobile, while remaining short enough to feel responsive.
        /// </summary>
        public const double MinimumDistance = 72;

        /// <summary>
        /// Returns true only for a deliberate leftward gesture. Requiring horizontal movement to
        /// dominate keeps normal timeline scrolling from opening the reply composer.
        /// </summary>
        public static bool ShouldReply(double horizontalDistance, double verticalDistance)
        {
            return horizontalDistance <= -MinimumDistance &&
                   Math.Abs(horizontalDistance) > Math.Abs(verticalDistance);
        }
    }
}
