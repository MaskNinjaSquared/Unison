// =============================================================================
// ProfilePictureCachePolicy
//
// How long a profile-picture IQ answer is reused before asking the server again.
// A picture lives long (its CDN URL outlasts this); "no picture" lives short so a
// new photo shows up without a notification; timeouts and errors are not kept.
// Opening a chat revalidates past this (see MobileChatListAvatarGate).
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class ProfilePictureCachePolicy
    {
        public static readonly TimeSpan FoundTtl = TimeSpan.FromHours(6);

        public static readonly TimeSpan NotFoundTtl = TimeSpan.FromMinutes(30);

        /// <summary>Reopening the same chat inside this window reuses the last revalidation.</summary>
        public static readonly TimeSpan RevalidateCooldown = TimeSpan.FromSeconds(60);

        public static TimeSpan? TtlFor(bool hasUrl, bool isNotFound)
        {
            if (hasUrl)
            {
                return FoundTtl;
            }

            return isNotFound ? NotFoundTtl : (TimeSpan?)null;
        }
    }
}
