// =============================================================================
// GroupCreatedBySelf
//
// Whether w:g2 creator / creator_pn is this account, and the Info caption with
// the group's creation date. SuperAdmin on the roster is not a substitute.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class GroupCreatedBySelf
    {
        /// <summary>
        /// Builds the localized caption when <paramref name="owner"/> or
        /// <paramref name="ownerPn"/> is self-linked and creation is known.
        /// <paramref name="template"/> must contain <c>{0}</c> for the short local date.
        /// </summary>
        public static string TryFormat(
            string owner,
            string ownerPn,
            long creationUnixSeconds,
            Func<string, bool> isSelfLinked,
            string template)
        {
            if (isSelfLinked == null ||
                creationUnixSeconds <= 0 ||
                string.IsNullOrWhiteSpace(template) ||
                !IsOwnedBySelf(owner, ownerPn, isSelfLinked))
            {
                return null;
            }

            string date = FormatShortLocalDate(creationUnixSeconds);
            if (string.IsNullOrEmpty(date))
            {
                return null;
            }

            try
            {
                return string.Format(template, date);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public static bool IsOwnedBySelf(
            string owner,
            string ownerPn,
            Func<string, bool> isSelfLinked)
        {
            if (isSelfLinked == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(owner) && isSelfLinked(owner))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(ownerPn) && isSelfLinked(ownerPn))
            {
                return true;
            }

            return false;
        }

        public static string FormatShortLocalDate(long creationUnixSeconds)
        {
            if (creationUnixSeconds <= 0)
            {
                return null;
            }

            try
            {
                return DateTimeOffset
                    .FromUnixTimeSeconds(creationUnixSeconds)
                    .ToLocalTime()
                    .ToString("d");
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}
