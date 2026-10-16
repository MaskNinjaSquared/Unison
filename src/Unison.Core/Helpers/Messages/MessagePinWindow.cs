// =============================================================================
// MessagePinWindow
//
// When a message pinned in its chat was pinned and when the pin runs out.
// A pin that arrives uses the sender's clock when it has one; a pin made here
// starts now. Either way a duration of zero means the pin does not expire, and
// an unpin has no expiry at all.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public struct MessagePinWindow
    {
        public DateTime PinnedAtUtc;
        public DateTime? ExpiresAtUtc;

        public static MessagePinWindow FromIncoming(
            bool pin,
            long senderTimestampMs,
            uint durationSeconds,
            DateTime nowUtc)
        {
            DateTime pinnedAt = senderTimestampMs > 0
                ? UnixTime.FromMilliseconds(senderTimestampMs)
                : nowUtc;
            return Create(pin, pinnedAt, durationSeconds);
        }

        public static MessagePinWindow FromLocal(bool pin, uint durationSeconds, DateTime nowUtc) =>
            Create(pin, nowUtc, durationSeconds);

        private static MessagePinWindow Create(bool pin, DateTime pinnedAtUtc, uint durationSeconds) =>
            new MessagePinWindow
            {
                PinnedAtUtc = pinnedAtUtc,
                ExpiresAtUtc = pin && durationSeconds > 0
                    ? pinnedAtUtc.AddSeconds(durationSeconds)
                    : (DateTime?)null
            };
    }
}
