// =============================================================================
// Tests for MessageBubbleContextGestureGate.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MessageBubbleContextGestureGateTests
    {
        [Fact]
        public void A_right_tap_alone_opens_the_menu()
        {
            var gate = new MessageBubbleContextGestureGate();

            Assert.True(gate.TryBeginFromRightTap());
        }

        [Fact]
        public void Holding_then_right_tap_opens_only_once()
        {
            // Mobile mouse / long-press: Holding.Started then RightTapped for one gesture.
            var gate = new MessageBubbleContextGestureGate();

            Assert.True(gate.TryBeginFromHold());
            Assert.False(gate.TryBeginFromRightTap());
        }

        [Fact]
        public void Right_tap_then_holding_opens_only_once()
        {
            // Some Mobile builds raise RightTapped before Holding for the same press.
            var gate = new MessageBubbleContextGestureGate();

            Assert.True(gate.TryBeginFromRightTap());
            Assert.False(gate.TryBeginFromHold());
        }

        [Fact]
        public void A_later_right_tap_opens_again()
        {
            var gate = new MessageBubbleContextGestureGate();

            Assert.True(gate.TryBeginFromHold());
            Assert.False(gate.TryBeginFromRightTap());

            // Past the duplicate window the next deliberate open must succeed.
            System.Threading.Thread.Sleep(
                (int)MessageBubbleContextGestureGate.DuplicateOpenWindow.TotalMilliseconds + 50);

            Assert.True(gate.TryBeginFromRightTap());
        }

        [Fact]
        public void The_first_scrim_tap_after_open_is_swallowed()
        {
            var gate = new MessageBubbleContextGestureGate();
            DateTime opened = DateTime.UtcNow;

            gate.TryBeginFromHold();

            Assert.False(gate.ShouldDismissFromScrim(opened));
            Assert.True(
                gate.ShouldDismissFromScrim(
                    opened.Add(MessageBubbleContextGestureGate.ScrimDismissGrace).AddMilliseconds(50)));
        }

        [Fact]
        public void Scrim_taps_inside_the_grace_window_are_ignored_even_after_the_first()
        {
            var gate = new MessageBubbleContextGestureGate();
            DateTime opened = DateTime.UtcNow;

            gate.TryBeginFromRightTap();

            Assert.False(gate.ShouldDismissFromScrim(opened));
            Assert.False(
                gate.ShouldDismissFromScrim(
                    opened.Add(MessageBubbleContextGestureGate.ScrimDismissGrace).AddMilliseconds(-1)));
            Assert.True(
                gate.ShouldDismissFromScrim(
                    opened.Add(MessageBubbleContextGestureGate.ScrimDismissGrace).AddMilliseconds(50)));
        }
    }
}
