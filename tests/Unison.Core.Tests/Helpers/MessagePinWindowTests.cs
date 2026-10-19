// =============================================================================
// Tests for MessagePinWindow: when a pinned message was pinned and when it expires.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MessagePinWindowTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Incoming_pin_starts_at_the_sender_clock()
        {
            MessagePinWindow window = MessagePinWindow.FromIncoming(
                pin: true,
                senderTimestampMs: 1_700_000_000_000L,
                durationSeconds: 60,
                nowUtc: Now);

            DateTime sent = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L).UtcDateTime;
            Assert.Equal(sent, window.PinnedAtUtc);
            Assert.Equal(sent.AddSeconds(60), window.ExpiresAtUtc);
        }

        [Fact]
        public void Incoming_pin_without_sender_clock_starts_now()
        {
            MessagePinWindow window = MessagePinWindow.FromIncoming(true, 0, 60, Now);

            Assert.Equal(Now, window.PinnedAtUtc);
            Assert.Equal(Now.AddSeconds(60), window.ExpiresAtUtc);
        }

        [Fact]
        public void Incoming_pin_without_duration_never_expires()
        {
            MessagePinWindow window = MessagePinWindow.FromIncoming(true, 0, 0, Now);

            Assert.Null(window.ExpiresAtUtc);
        }

        [Fact]
        public void Incoming_unpin_has_no_expiry()
        {
            MessagePinWindow window = MessagePinWindow.FromIncoming(false, 0, 604800, Now);

            Assert.Null(window.ExpiresAtUtc);
        }

        [Fact]
        public void Local_pin_starts_now_and_runs_for_the_duration()
        {
            MessagePinWindow window = MessagePinWindow.FromLocal(true, 604800, Now);

            Assert.Equal(Now, window.PinnedAtUtc);
            Assert.Equal(Now.AddSeconds(604800), window.ExpiresAtUtc);
        }

        [Fact]
        public void Local_unpin_has_no_expiry()
        {
            MessagePinWindow window = MessagePinWindow.FromLocal(false, 604800, Now);

            Assert.Equal(Now, window.PinnedAtUtc);
            Assert.Null(window.ExpiresAtUtc);
        }

        [Fact]
        public void Local_pin_without_duration_never_expires()
        {
            // The local path used to expire such a pin at the moment it was made.
            MessagePinWindow window = MessagePinWindow.FromLocal(true, 0, Now);

            Assert.Null(window.ExpiresAtUtc);
        }
    }
}
