using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Sizes and deletes chat media under LocalFolder. Conversations stay in SQLite.
    /// Each row is marked deleted before its file is removed, so a retry skips it.
    /// </summary>
    public sealed class MediaDiskUsageService : IMediaDiskUsageService
    {
        private readonly IHistoryMessageStore _store;

        public MediaDiskUsageService(IHistoryMessageStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public async Task<MediaDiskUsage> MeasureAsync()
        {
            IReadOnlyList<CachedMediaEntry> entries = await _store.ListCachedMediaAsync().ConfigureAwait(false);
            IReadOnlyList<string> uris = MediaCacheCleanPlan.DistinctUris(entries);
            long total = 0;
            for (int i = 0; i < uris.Count; i++)
            {
                total += await SizeAsync(uris[i]).ConfigureAwait(false);
            }

            return new MediaDiskUsage
            {
                TotalBytes = total,
                FileCount = uris.Count
            };
        }

        public async Task CleanAsync()
        {
            IReadOnlyList<CachedMediaEntry> entries = await _store.ListCachedMediaAsync().ConfigureAwait(false);
            if (entries.Count == 0)
            {
                return;
            }

            await _store.MarkMediaDeletedAsync(entries).ConfigureAwait(false);
            IReadOnlyList<string> uris = MediaCacheCleanPlan.DistinctUris(entries);
            for (int i = 0; i < uris.Count; i++)
            {
                await DeleteOneAsync(uris[i]).ConfigureAwait(false);
            }
        }

        private static async Task<long> SizeAsync(string uri)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(uri));
                BasicProperties props = await file.GetBasicPropertiesAsync();
                return (long)props.Size;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MediaDiskUsage] Size skipped: " + ex.Message);
                return 0;
            }
        }

        private static async Task DeleteOneAsync(string uri)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(uri));
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MediaDiskUsage] Delete skipped: " + ex.Message);
            }
        }
    }
}
