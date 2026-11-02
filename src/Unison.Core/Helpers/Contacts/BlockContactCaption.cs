// =============================================================================
// BlockContactCaption
//
// Info "Block (Name)" label without the unsaved-contact ~ prefix.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class BlockContactCaption
    {
        /// <summary>
        /// Builds the block-row label. Prefer stripped display name, then phone.
        /// <paramref name="namedTemplate"/> must include <c>{0}</c> for the name.
        /// </summary>
        public static string Format(
            string displayName,
            string phoneFallback,
            string namedTemplate,
            string bareFallback)
        {
            string name = DisplayNameResolution.StripContactIndicator(displayName);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = string.IsNullOrWhiteSpace(phoneFallback) ? null : phoneFallback.Trim();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return string.IsNullOrWhiteSpace(bareFallback) ? "Block" : bareFallback;
            }

            if (string.IsNullOrWhiteSpace(namedTemplate))
            {
                return "Block (" + name + ")";
            }

            try
            {
                return string.Format(namedTemplate, name);
            }
            catch (FormatException)
            {
                return "Block (" + name + ")";
            }
        }
    }
}
