// =============================================================================
// WebPDisplayName
//
// The file base a cached WebP uses for its PNG sibling. A name that is already
// the sibling is left alone, so a repair pass cannot nest _display_display.
// =============================================================================
using System;
using System.IO;

namespace Unison.Core.Helpers
{
    public static class WebPDisplayName
    {
        public static string FileBaseOrSkip(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return null;
            }

            string fileName = uri;
            int slash = uri.LastIndexOf('/');
            if (slash >= 0 && slash < uri.Length - 1)
            {
                fileName = uri.Substring(slash + 1);
            }

            string fileBase = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(fileBase) ||
                fileBase.EndsWith("_display", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return fileBase;
        }
    }
}
