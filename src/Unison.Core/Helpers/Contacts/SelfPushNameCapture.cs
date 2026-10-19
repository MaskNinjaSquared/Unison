// =============================================================================
// SelfPushNameCapture
//
// What to do with a push name that claims to be the logged-in account.
//
// A dash is a placeholder the server emits, not a rename. And once a real name
// is on file, only the app-state pushNameSetting — the user renaming themselves
// on another device — may replace it. Every other path keeps the name already
// trusted, so the credentials and the sidebar stay in step.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class SelfPushNameCapture
    {
        public static bool IsDashPlaceholder(string pushName)
        {
            return string.Equals(pushName == null ? null : pushName.Trim(), "-", StringComparison.Ordinal);
        }

        /// <summary>
        /// The name to store. When a different name is already trusted and this update
        /// is not a deliberate rename, the trusted one wins.
        /// </summary>
        public static string PreferEstablished(string established, string offered, bool fromAppState)
        {
            if (!string.IsNullOrWhiteSpace(established) &&
                !string.Equals(established, offered, StringComparison.Ordinal) &&
                !fromAppState)
            {
                return established;
            }

            return offered;
        }
    }
}
