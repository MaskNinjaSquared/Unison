// =============================================================================
// NewsletterPictureUrl
//
// Channel picture from newsletterMetadata is often a direct_path under
// pps.whatsapp.net (same CDN host profile-picture IQ URLs use).
// =============================================================================
using System;

namespace Unison.Socket.UseCases.Newsletter
{
    public static class NewsletterPictureUrl
    {
        public const string PpsHost = "https://pps.whatsapp.net";

        public static string Resolve(string url, string directPath)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                string trimmedUrl = url.Trim();
                if (trimmedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    trimmedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmedUrl;
                }
            }

            if (string.IsNullOrWhiteSpace(directPath))
            {
                return null;
            }

            string path = directPath.Trim();
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                path = "/" + path;
            }

            return PpsHost + path;
        }
    }
}
