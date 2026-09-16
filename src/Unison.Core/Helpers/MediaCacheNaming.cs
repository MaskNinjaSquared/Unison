// =============================================================================
// MediaCacheNaming
//
// What a cached media file is called on disk.
//
// The name is derived from the message's encrypted-content hash rather than its
// id, so the same media downloaded twice lands on the same file instead of
// being stored again under a new name. The hash is base64, which contains '+'
// and '/' and is therefore not usable in a file name -- hence the URL-safe
// alphabet.
//
// This was written out by hand in six places, in two variants (one taking the
// hash as bytes, one as an already-encoded string) that had drifted in what
// they fell back to when the hash was missing.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class MediaCacheNaming
    {
        /// <summary>
        /// Longest file base we will produce. Keeps the full path clear of the platform
        /// limit once the cache folder, a suffix and an extension are added.
        /// </summary>
        public const int MaxFileBaseLength = 80;

        /// <summary>
        /// Rewrites standard base64 into the URL-safe alphabet and drops the padding, so
        /// the result is usable as a file name.
        /// </summary>
        public static string ToUrlSafeBase64(string standardBase64)
        {
            if (string.IsNullOrWhiteSpace(standardBase64))
            {
                return string.Empty;
            }

            return standardBase64.Trim().Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        /// <summary>Same, for a hash still held as bytes.</summary>
        public static string ToUrlSafeBase64(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return string.Empty;
            }

            return ToUrlSafeBase64(Convert.ToBase64String(data));
        }

        /// <summary>
        /// The file base for a piece of media: its content hash when known, otherwise the
        /// message id, otherwise a fresh random name.
        /// </summary>
        /// <remarks>
        /// The fallbacks matter. Without a hash two downloads of the same media cannot be
        /// recognised as the same file, so the message id keeps them together at least
        /// within one message. The random name is the last resort and deliberately never
        /// collides: sharing a name with unrelated media would serve the wrong file.
        /// </remarks>
        public static string ResolveFileBase(byte[] contentHash, string messageId)
        {
            string fromHash = ToUrlSafeBase64(contentHash);
            if (!string.IsNullOrEmpty(fromHash))
            {
                return Sanitize(fromHash);
            }

            return Sanitize(messageId);
        }

        /// <summary>Same, for a hash that arrives already base64-encoded.</summary>
        public static string ResolveFileBase(string contentHashBase64, string messageId)
        {
            string fromHash = ToUrlSafeBase64(contentHashBase64);
            if (!string.IsNullOrEmpty(fromHash))
            {
                return Sanitize(fromHash);
            }

            return Sanitize(messageId);
        }

        /// <summary>
        /// Replaces anything that cannot appear in a file name and caps the length. An
        /// empty input becomes a random name rather than an empty file name.
        /// </summary>
        public static string Sanitize(string fileBase)
        {
            if (string.IsNullOrWhiteSpace(fileBase))
            {
                return Guid.NewGuid().ToString("N");
            }

            char[] chars = fileBase.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                {
                    chars[i] = '_';
                }
            }

            string sanitized = new string(chars);
            return sanitized.Length > MaxFileBaseLength
                ? sanitized.Substring(0, MaxFileBaseLength)
                : sanitized;
        }
    }
}
