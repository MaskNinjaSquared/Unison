// =============================================================================
// MediaHttp
//
// The HTTP side of media: one shared client, the headers the CDN expects, and
// the base64 dialect its URLs are written in.
//
// Media is the one part of the protocol that does not travel over the socket,
// so this is where the stack talks plain HTTPS. The client is shared and never
// disposed on purpose: a new HttpClient per transfer leaks sockets in TIME_WAIT
// for as long as the OS decides, which shows up as failing uploads on a slow
// connection long after the code that caused it has moved on.
//
// Ports: rc14 DEFAULT_ORIGIN, getHttpStream and
// encodeBase64EncodedStringForUpload in src/Utils/messages-media.ts
// =============================================================================
using System;
using System.Net.Http;

namespace Unison.Socket.Media
{
    public static class MediaHttp
    {
        /// <summary>The CDN checks this and refuses requests that do not look like the web client.</summary>
        public const string Origin = "https://web.whatsapp.com";

        /// <summary>Host that serves a direct path when the message carries no absolute URL.</summary>
        public const string DownloadHost = "mmg.whatsapp.net";

        private static readonly Lazy<HttpClient> Shared = new Lazy<HttpClient>(Create);

        public static HttpClient Client
        {
            get { return Shared.Value; }
        }

        /// <summary>
        /// A message carries either an absolute URL or a path to resolve against the media host.
        /// The path is preferred: URLs in older messages point at hosts that have since gone away,
        /// while a direct path is resolved fresh every time.
        /// </summary>
        public static string ResolveDownloadUrl(string directPath, string url)
        {
            if (!string.IsNullOrEmpty(directPath))
            {
                return "https://" + DownloadHost + directPath;
            }

            if (!string.IsNullOrEmpty(url))
            {
                return url;
            }

            return null;
        }

        /// <summary>
        /// The upload URL carries the file's encrypted digest twice, as the name and as the token,
        /// in the URL-safe base64 dialect without padding.
        /// </summary>
        public static string EncodeForUpload(byte[] value)
        {
            if (value == null || value.Length == 0)
            {
                return string.Empty;
            }

            return Convert.ToBase64String(value)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        private static HttpClient Create()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", Origin);
            client.Timeout = TimeSpan.FromMinutes(5);

            return client;
        }
    }
}
