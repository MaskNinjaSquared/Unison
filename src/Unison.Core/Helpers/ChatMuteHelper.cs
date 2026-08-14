using System;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Local/server mute deadline helpers (<see cref="Models.ChatItem.MutedUntil"/> unix seconds).
    /// </summary>
    public static class ChatMuteHelper
    {
        public static readonly TimeSpan EightHours = TimeSpan.FromHours(8);
        public static readonly TimeSpan OneWeek = TimeSpan.FromDays(7);

        /// <summary>Sentinel for "mute forever" (year 2999 UTC).</summary>
        public static readonly long ForeverUnixSeconds =
            new DateTimeOffset(2999, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        public static long FromNow(TimeSpan duration)
        {
            long seconds = (long)Math.Max(0, duration.TotalSeconds);
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds;
        }

        /// <summary>
        /// Not muted: null or expired (<c>until &lt;= now</c>).
        /// Muted: <c>0</c> (WhatsApp forever) or <c>until &gt; now</c>.
        /// </summary>
        public static bool IsMuted(long? mutedUntilUnixSeconds)
        {
            if (!mutedUntilUnixSeconds.HasValue)
            {
                return false;
            }

            long until = mutedUntilUnixSeconds.Value;
            if (until == 0)
            {
                return true;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return until > now;
        }
    }
}
