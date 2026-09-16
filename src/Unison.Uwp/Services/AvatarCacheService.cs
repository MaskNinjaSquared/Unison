// =============================================================================
// AvatarCacheService
//
// The avatar half of LocalFolder/MediaCache. One file per JID per variant, named
// from the JID so a lookup never needs an index, and addressed from XAML by the
// ms-appdata URI this returns.
//
// Extracted from WhatsAppService (extraction phase 3.1): nothing here needs the
// socket, the chat list or the UI thread, so nothing here needed to live in the
// client.
// =============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.Storage;

namespace Unison.Uwp.Services
{
    public sealed class AvatarCacheService : IAvatarCache
    {
        private const string MediaFolderName = "MediaCache";
        private const string AvatarFolderName = "Avatars";
        private const string LocalUriPrefix = "ms-appdata:///local/" + MediaFolderName + "/" + AvatarFolderName + "/";
        private const string HighResolutionSuffix = "_high";
        private const int MaxFileNameStemLength = 96;

        /// <summary>
        /// Short on purpose. An avatar that is slow is not worth holding a background batch open
        /// for; the row keeps its placeholder and the next pass tries again.
        /// </summary>
        private static readonly HttpClient AvatarHttpClient =
            new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        public bool TryGet(string jid, AvatarVariant variant, out string localUri, out DateTime fetchedAtUtc)
        {
            localUri = null;
            fetchedAtUtc = DateTime.MinValue;

            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            try
            {
                string fileName = BuildFileName(jid, variant);
                string filePath = Path.Combine(
                    ApplicationData.Current.LocalFolder.Path,
                    MediaFolderName,
                    AvatarFolderName,
                    fileName);

                if (!File.Exists(filePath))
                {
                    return false;
                }

                localUri = LocalUriPrefix + fileName;
                fetchedAtUtc = File.GetLastWriteTimeUtc(filePath);
                if (fetchedAtUtc == DateTime.MinValue)
                {
                    fetchedAtUtc = DateTime.UtcNow;
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AvatarCache] Failed to check cached avatar for {jid}: {ex.Message}");
                return false;
            }
        }

        public async Task<string> SaveAsync(
            string jid,
            string remoteUrl,
            AvatarVariant variant,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                return null;
            }

            token.ThrowIfCancellationRequested();
            byte[] bytes = await AvatarHttpClient.GetByteArrayAsync(remoteUrl);
            token.ThrowIfCancellationRequested();

            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            var local = ApplicationData.Current.LocalFolder;
            var mediaFolder = await local.CreateFolderAsync(MediaFolderName, CreationCollisionOption.OpenIfExists);
            var avatarFolder = await mediaFolder.CreateFolderAsync(AvatarFolderName, CreationCollisionOption.OpenIfExists);
            string fileName = BuildFileName(jid, variant);
            var file = await avatarFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, bytes);

            string localUri = LocalUriPrefix + fileName;
            Debug.WriteLine($"[AvatarCache] Cached avatar for {jid}: bytes={bytes.Length}, uri={localUri}");
            return localUri;
        }

        public void DeleteIfCached(string localUri)
        {
            if (string.IsNullOrWhiteSpace(localUri) ||
                !localUri.StartsWith(LocalUriPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string fileName = localUri.Substring(LocalUriPrefix.Length);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            try
            {
                string filePath = Path.Combine(
                    ApplicationData.Current.LocalFolder.Path,
                    MediaFolderName,
                    AvatarFolderName,
                    fileName);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AvatarCache] Failed to remove broken avatar {fileName}: {ex.Message}");
            }
        }

        /// <summary>
        /// The JID is the index: every non-alphanumeric character collapses to an underscore, so
        /// the same JID always resolves to the same file without a lookup table.
        /// </summary>
        private static string BuildFileName(string jid, AvatarVariant variant)
        {
            string source = string.IsNullOrWhiteSpace(jid) ? Guid.NewGuid().ToString("N") : jid;
            var chars = source
                .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray();
            string stem = new string(chars).Trim('_');
            if (string.IsNullOrWhiteSpace(stem))
            {
                stem = Guid.NewGuid().ToString("N");
            }

            if (stem.Length > MaxFileNameStemLength)
            {
                stem = stem.Substring(0, MaxFileNameStemLength);
            }

            return variant == AvatarVariant.HighResolution
                ? stem + HighResolutionSuffix + ".jpg"
                : stem + ".jpg";
        }
    }
}
