using System;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Parses secondary-tile / toast launch arguments
    /// (<c>notification=1&amp;chat={jid}</c>).
    /// </summary>
    public static class LaunchActivationHelper
    {
        /// <summary>Returns the chat JID from activation arguments, or null.</summary>
        public static string TryGetChatJid(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return null;
            }

            string raw = arguments.Trim();
            // Defensive: some hosts pass "?notification=1&chat=..."
            if (raw.Length > 0 && raw[0] == '?')
            {
                raw = raw.Substring(1);
            }

            string[] parts = raw.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                int eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string key = part.Substring(0, eq).Trim();
                if (!string.Equals(key, "chat", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = part.Substring(eq + 1).Trim();
                if (value.Length == 0)
                {
                    return null;
                }

                try
                {
                    return Uri.UnescapeDataString(value);
                }
                catch
                {
                    return value;
                }
            }

            return null;
        }
    }
}
