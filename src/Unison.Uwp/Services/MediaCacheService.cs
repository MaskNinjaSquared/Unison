// =============================================================================
// MediaCacheService
//
// LocalFolder/MediaCache/{Images,Audio,Documents,Video,VideoPosters}. Extracted
// from WhatsAppService.Media, where the two-line "open MediaCache, then open the
// subfolder" dance and the matching ms-appdata URI were written out by hand at
// every save and every lookup.
//
// File names are derived from the message id, which is why saving is idempotent
// and why a redownload of the same attachment can be skipped rather than paid
// for twice.
// =============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.Storage;

namespace Unison.Uwp.Services
{
    public sealed class MediaCacheService : IMediaCache
    {
        private const string RootFolderName = "MediaCache";
        private const string UriPrefix = "ms-appdata:///local/" + RootFolderName + "/";

        /// <summary>
        /// StorageFile names reject the path and base64url characters that message ids are full
        /// of. The cap is for the filesystem, not for uniqueness: ids are far shorter than this,
        /// and anything longer is not an id.
        /// </summary>
        private const int MaxFileBaseLength = 80;

        public async Task<string> SaveAsync(
            MediaCacheKind kind,
            string fileBase,
            string extension,
            byte[] bytes,
            bool reuseExisting = true,
            CancellationToken token = default(CancellationToken))
        {
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            try
            {
                string fileName = BuildFileName(fileBase, extension);
                StorageFolder folder = await OpenFolderAsync(kind).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (!reuseExisting || await folder.TryGetItemAsync(fileName) == null)
                {
                    StorageFile file = await folder.CreateFileAsync(
                        fileName,
                        CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteBytesAsync(file, bytes);
                }

                return BuildUri(kind, fileName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCacheService] Save failed ({kind}): {ex.Message}");
                return null;
            }
        }

        public async Task<string> TryGetUriAsync(MediaCacheKind kind, string fileBase, string extension)
        {
            try
            {
                string fileName = BuildFileName(fileBase, extension);
                StorageFolder folder = await OpenFolderAsync(kind).ConfigureAwait(false);
                if (await folder.TryGetItemAsync(fileName) is StorageFile)
                {
                    return BuildUri(kind, fileName);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCacheService] Lookup failed ({kind}): {ex.Message}");
            }

            return null;
        }

        public async Task<byte[]> TryReadAsync(string cacheUri)
        {
            if (string.IsNullOrWhiteSpace(cacheUri))
            {
                return null;
            }

            try
            {
                StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(cacheUri));
                var buffer = await FileIO.ReadBufferAsync(file);
                return buffer.ToArray();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MediaCacheService] Read failed: " + ex.Message);
                return null;
            }
        }

        public string SanitizeFileBase(string fileBase) =>
            string.IsNullOrWhiteSpace(fileBase) ? Guid.NewGuid().ToString("N") : Sanitize(fileBase);

        internal string BuildUri(MediaCacheKind kind, string fileName) =>
            UriPrefix + SubfolderName(kind) + "/" + fileName;

        internal string BuildFileName(string fileBase, string extension) =>
            SanitizeFileBase(fileBase) + (extension ?? string.Empty);

        private static async Task<StorageFolder> OpenFolderAsync(MediaCacheKind kind)
        {
            StorageFolder root = await ApplicationData.Current.LocalFolder
                .CreateFolderAsync(RootFolderName, CreationCollisionOption.OpenIfExists);
            return await root.CreateFolderAsync(
                SubfolderName(kind),
                CreationCollisionOption.OpenIfExists);
        }

        private static string SubfolderName(MediaCacheKind kind)
        {
            switch (kind)
            {
                case MediaCacheKind.Image: return "Images";
                case MediaCacheKind.Audio: return "Audio";
                case MediaCacheKind.Document: return "Documents";
                case MediaCacheKind.Video: return "Video";
                case MediaCacheKind.VideoPoster: return "VideoPosters";
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static string Sanitize(string fileBase)
        {
            var chars = fileBase.ToCharArray();
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
