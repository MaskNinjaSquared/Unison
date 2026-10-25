// =============================================================================
// AvatarPictureIdentity
//
// After a profile-picture IQ, decide whether the CDN bytes are worth fetching.
// Prefer the server picture id; fall back to the remote URL when id is absent.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class AvatarPictureIdentity
    {
        /// <summary>
        /// True when the next answer describes the same picture we already cached.
        /// Unknown identity is treated as changed so a first open still downloads.
        /// </summary>
        public static bool IsUnchanged(
            string previousId,
            string previousRemoteUrl,
            string nextId,
            string nextRemoteUrl)
        {
            if (!string.IsNullOrWhiteSpace(previousId) && !string.IsNullOrWhiteSpace(nextId))
            {
                return string.Equals(previousId.Trim(), nextId.Trim(), StringComparison.Ordinal);
            }

            if (!string.IsNullOrWhiteSpace(previousRemoteUrl) && !string.IsNullOrWhiteSpace(nextRemoteUrl))
            {
                return string.Equals(
                    previousRemoteUrl.Trim(),
                    nextRemoteUrl.Trim(),
                    StringComparison.Ordinal);
            }

            return false;
        }
    }
}
