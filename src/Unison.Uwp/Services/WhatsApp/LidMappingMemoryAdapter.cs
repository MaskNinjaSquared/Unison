using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Socket.Signal;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Sync peek + fire-and-forget write of <see cref="LidMappingStore"/> for
    /// <see cref="Unison.Core.State.JidAliasTable"/>.
    /// </summary>
    public sealed class LidMappingMemoryAdapter : ILidMappingMemory
    {
        private readonly LidMappingStore _store;

        public LidMappingMemoryAdapter(LidMappingStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public bool TryGetPairedJid(string normalizedJid, out string pairedJid)
        {
            pairedJid = null;
            if (string.IsNullOrEmpty(normalizedJid))
            {
                return false;
            }

            // Store owns user-part keys + device/hosted re-attach (3.7b key shapes).
            return _store.TryGetCachedPairedJid(JidHelper.Normalize(normalizedJid), out pairedJid);
        }

        public void RememberPair(string jidA, string jidB)
        {
            Remember(new[] { new KeyValuePair<string, string>(jidA, jidB) }, cacheInMemory: true);
        }

        /// <summary>
        /// Bulk import of the alias table's own snapshot: the table already holds these in
        /// RAM, so they go to storage only and do not churn the store's memory cache.
        /// </summary>
        public void RememberPairs(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            Remember(pairs, cacheInMemory: false);
        }

        private void Remember(IEnumerable<KeyValuePair<string, string>> pairs, bool cacheInMemory)
        {
            if (pairs == null)
            {
                return;
            }

            var mappings = new List<LidMapping>();
            foreach (KeyValuePair<string, string> pair in pairs)
            {
                LidMapping mapping = LidMapping.TryFromAliasPair(
                    JidHelper.Normalize(pair.Key),
                    JidHelper.Normalize(pair.Value));
                if (mapping != null)
                {
                    mappings.Add(mapping);
                }
            }

            if (mappings.Count == 0)
            {
                return;
            }

            // Same posture as SocketBridge.Store: announce from sync paths, do not await.
            _ = ObserveStoreAsync(_store.StoreMappingsAsync(mappings, cacheInMemory));
        }

        private static async Task ObserveStoreAsync(Task write)
        {
            try
            {
                await write.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[LidMappingMemory] RememberPair store failed: " + ex.Message);
            }
        }
    }
}
