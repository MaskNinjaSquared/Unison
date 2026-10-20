// =============================================================================
// MessageBubbleContextGestureGate
//
// UWP raises Holding and RightTapped for the same long-press / mouse-right
// gesture on Mobile. Opening the bubble menu from both stacks two MenuFlyouts
// (Unison) or Select→Dismiss on the same bubble (Legacy command bar). After a
// hold opens the Legacy scrim under the finger, the release Tapped dismisses
// the bar immediately — swallow that first dismiss and a short grace window.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public sealed class MessageBubbleContextGestureGate
    {
        /// <summary>
        /// Same-gesture Holding + RightTapped (either order) lands inside this window.
        /// </summary>
        public static readonly TimeSpan DuplicateOpenWindow = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// How long after open a scrim tap is still treated as the gesture that opened the bar.
        /// </summary>
        public static readonly TimeSpan ScrimDismissGrace = TimeSpan.FromMilliseconds(500);

        private bool _suppressNextRightTap;
        private int _suppressScrimDismisses;
        private DateTime _openedUtc = DateTime.MinValue;

        /// <summary>
        /// Holding.Started should open the menu once and arm RightTapped suppression.
        /// </summary>
        public bool TryBeginFromHold()
        {
            DateTime now = DateTime.UtcNow;
            if (IsDuplicateOpen(now))
            {
                _suppressNextRightTap = true;
                return false;
            }

            _suppressNextRightTap = true;
            ArmScrimGuard(now);
            return true;
        }

        /// <summary>
        /// RightTapped should open only when it is not the echo of a Holding that already did.
        /// </summary>
        public bool TryBeginFromRightTap()
        {
            if (_suppressNextRightTap)
            {
                _suppressNextRightTap = false;
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if (IsDuplicateOpen(now))
            {
                return false;
            }

            ArmScrimGuard(now);
            return true;
        }

        /// <summary>
        /// True when a scrim tap should dismiss the bar.
        /// </summary>
        public bool ShouldDismissFromScrim(DateTime utcNow)
        {
            if (_suppressScrimDismisses > 0)
            {
                _suppressScrimDismisses--;
                return false;
            }

            if (_openedUtc != DateTime.MinValue &&
                utcNow - _openedUtc < ScrimDismissGrace)
            {
                return false;
            }

            return true;
        }

        private bool IsDuplicateOpen(DateTime utcNow)
        {
            return _openedUtc != DateTime.MinValue &&
                   utcNow - _openedUtc < DuplicateOpenWindow;
        }

        private void ArmScrimGuard(DateTime utcNow)
        {
            _openedUtc = utcNow;
            _suppressScrimDismisses = 1;
        }
    }
}
