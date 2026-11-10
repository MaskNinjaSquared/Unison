using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Signal;
using Xunit;

namespace Unison.Core.Tests.Signal
{
    public sealed class LidMappingStoreBulkTests
    {
        private static readonly LidMapping Pair = new LidMapping("111@lid", "5511111111111@s.whatsapp.net");

        [Fact]
        public async Task Bulk_write_through_skips_memory_cache_until_async_read()
        {
            var storage = new InMemoryLidMappingStorage();
            var store = new LidMappingStore(storage);

            await store.StoreMappingsAsync(new[] { Pair }, cacheInMemory: false);

            IDictionary<string, string> all = await storage.GetAllAsync();
            Assert.Equal("111", all["5511111111111"]);
            Assert.False(store.TryGetCachedPairedJid("111@lid", out _));

            Assert.Equal("5511111111111@s.whatsapp.net", await store.GetPnForLidAsync("111@lid"));
            Assert.True(store.TryGetCachedPairedJid("111@lid", out string pn));
            Assert.Equal("5511111111111@s.whatsapp.net", pn);
        }

        [Fact]
        public async Task Bulk_while_disk_held_stays_visible_through_pending_queue()
        {
            var storage = new InMemoryLidMappingStorage();
            var store = new LidMappingStore(storage, policy: new FixedPolicy(40, 1500));

            await store.StoreMappingsAsync(new[] { Pair }, cacheInMemory: false);

            Assert.Empty(await storage.GetAllAsync());
            Assert.True(store.TryGetCachedPairedJid("111@lid", out string pn));
            Assert.Equal("5511111111111@s.whatsapp.net", pn);
            Assert.True(store.TryGetCachedPairedJid("5511111111111@s.whatsapp.net", out string lid));
            Assert.Equal("111@lid", lid);
            Assert.Equal("111@lid", await store.GetLidForPnAsync("5511111111111@s.whatsapp.net"));
        }

        [Fact]
        public async Task Bulk_after_flush_is_served_from_disk_on_async_read()
        {
            var storage = new InMemoryLidMappingStorage();
            var store = new LidMappingStore(storage, policy: new FixedPolicy(40, 1500));

            await store.StoreMappingsAsync(new[] { Pair }, cacheInMemory: false);
            await store.EnableDiskPersistAsync("test");

            Assert.False(store.TryGetCachedPairedJid("111@lid", out _));
            Assert.Equal("5511111111111@s.whatsapp.net", await store.GetPnForLidAsync("111@lid"));
            Assert.True(store.TryGetCachedPairedJid("111@lid", out _));
        }

        [Fact]
        public async Task Large_cached_batch_respects_tier_cap()
        {
            var storage = new InMemoryLidMappingStorage();
            var store = new LidMappingStore(storage, policy: new FixedPolicy(10000, 1500, cachePairs: 100));
            List<LidMapping> pairs = Enumerable.Range(0, 1000)
                .Select(i => new LidMapping((1000 + i) + "@lid", "55119" + (1000000 + i) + "@s.whatsapp.net"))
                .ToList();

            await store.StoreMappingsAsync(pairs);

            Assert.True(store.TryGetCachedPairedJid("1999@lid", out _));
            Assert.True(store.MemoryCacheCount <= 200);
        }

        private sealed class FixedPolicy : ILidMappingPersistPolicy
        {
            public FixedPolicy(int batchSize, int delayMs, int cachePairs = 1024)
            {
                BatchSize = batchSize;
                DelayMs = delayMs;
                MemoryCacheMaxEntries = cachePairs * 2;
                WarmMaxPairs = 0;
            }

            public int BatchSize { get; }

            public int DelayMs { get; }

            public int MemoryCacheMaxEntries { get; }

            public int WarmMaxPairs { get; }
        }
    }
}
