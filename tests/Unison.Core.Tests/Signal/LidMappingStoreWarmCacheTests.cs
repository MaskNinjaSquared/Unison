using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Signal;
using Xunit;

namespace Unison.Core.Tests.Signal
{
    public sealed class LidMappingStoreWarmCacheTests
    {
        [Fact]
        public async Task WarmCacheFromStorageAsync_refills_memory_after_close()
        {
            var storage = new InMemoryLidMappingStorage();
            var writer = new LidMappingStore(storage);
            await writer.StoreMappingsAsync(new[]
            {
                new LidMapping("199999999999@lid", "5511999990000@s.whatsapp.net")
            });
            writer.Close();

            var reader = new LidMappingStore(storage);
            Assert.False(reader.TryGetCachedPairedJid("199999999999@lid", out _));

            // No policy: full in-memory load (tests). Production Mobile uses WarmMaxPairs=0.
            await reader.WarmCacheFromStorageAsync();

            Assert.True(reader.TryGetCachedPairedJid("199999999999@lid", out string pn));
            Assert.Equal("5511999990000@s.whatsapp.net", pn);
        }

        [Fact]
        public async Task WarmCacheFromStorageAsync_respects_zero_warm_policy()
        {
            var storage = new InMemoryLidMappingStorage();
            var writer = new LidMappingStore(storage);
            await writer.StoreMappingsAsync(new[]
            {
                new LidMapping("199999999999@lid", "5511999990000@s.whatsapp.net")
            });
            writer.Close();

            var reader = new LidMappingStore(
                storage,
                policy: new FixedWarmPolicy(warmMaxPairs: 0, memoryCacheMaxEntries: 1024));
            await reader.WarmCacheFromStorageAsync();

            Assert.False(reader.TryGetCachedPairedJid("199999999999@lid", out _));
        }

        [Fact]
        public async Task WarmCacheFromStorageAsync_caps_recent_window()
        {
            var storage = new InMemoryLidMappingStorage();
            var writer = new LidMappingStore(storage);
            await writer.StoreMappingsAsync(new[]
            {
                new LidMapping("111@lid", "5511111111111@s.whatsapp.net"),
                new LidMapping("222@lid", "5511222222222@s.whatsapp.net")
            });
            writer.Close();

            var reader = new LidMappingStore(
                storage,
                policy: new FixedWarmPolicy(warmMaxPairs: 1, memoryCacheMaxEntries: 1024));
            await reader.WarmCacheFromStorageAsync();

            int hits = 0;
            if (reader.TryGetCachedPairedJid("111@lid", out _))
            {
                hits++;
            }

            if (reader.TryGetCachedPairedJid("222@lid", out _))
            {
                hits++;
            }

            Assert.Equal(1, hits);
        }

        private sealed class FixedWarmPolicy : ILidMappingPersistPolicy
        {
            public FixedWarmPolicy(int warmMaxPairs, int memoryCacheMaxEntries)
            {
                WarmMaxPairs = warmMaxPairs;
                MemoryCacheMaxEntries = memoryCacheMaxEntries;
                BatchSize = 0;
                DelayMs = 0;
            }

            public int BatchSize { get; }

            public int DelayMs { get; }

            public int MemoryCacheMaxEntries { get; }

            public int WarmMaxPairs { get; }
        }
    }
}
