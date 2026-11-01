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

        /// <summary>
        /// 0..1 how far a leftward drag has gone toward <see cref="MinimumDistance"/>.
        /// Used to fade the reply circle beside the bubble during the sling.
        /// </summary>
        public static double Progress(double horizontalDistance)
        {
            if (horizontalDistance >= 0 || MinimumDistance <= 0)
            {
                return 0;
            }

            double progress = -horizontalDistance / MinimumDistance;
            if (progress <= 0)
            {
                return 0;
            }

            return progress >= 1 ? 1 : progress;
        }
    }
}
