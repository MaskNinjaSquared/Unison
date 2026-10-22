// =============================================================================
// WebPCacheUri
//
// Whether a cached media URI points at a WebP file (case-insensitive .webp).
// =============================================================================
using System;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    public static class WebPCacheUri
    {
        public static bool Matches(string uri)
        {
            return !string.IsNullOrWhiteSpace(uri)
                && uri.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
        }
    }
}
