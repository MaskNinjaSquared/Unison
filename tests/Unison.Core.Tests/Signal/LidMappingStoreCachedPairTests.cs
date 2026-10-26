using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Signal;
using Xunit;

namespace Unison.Core.Tests.Signal
{
    public sealed class LidMappingStoreCachedPairTests
    {
        [Fact]
        public async Task TryGetCachedPairedJid_reattaches_device_suffix()
        {
            var store = new LidMappingStore(new InMemoryLidMappingStorage());
            await store.StoreMappingsAsync(new[]
            {
                new LidMapping("199999999999@lid", "5511999990000@s.whatsapp.net")
            });

            Assert.True(store.TryGetCachedPairedJid("199999999999:1@lid", out string pn));
            Assert.Equal("5511999990000:1@s.whatsapp.net", pn);

            Assert.True(store.TryGetCachedPairedJid("5511999990000:2@s.whatsapp.net", out string lid));
            Assert.Equal("199999999999:2@lid", lid);
        }

        [Fact]
        public async Task TryGetCachedPairedJid_handles_hosted_pair()
        {
            var store = new LidMappingStore(new InMemoryLidMappingStorage());
            await store.StoreMappingsAsync(new[]
            {
                new LidMapping("199999999999:99@hosted.lid", "5511999990000:99@hosted")
            });

            Assert.True(store.TryGetCachedPairedJid("199999999999:99@hosted.lid", out string pn));
            Assert.Equal("5511999990000:99@hosted", pn);

            Assert.True(store.TryGetCachedPairedJid("5511999990000:99@hosted", out string lid));
            Assert.Equal("199999999999:99@hosted.lid", lid);
        }

        [Fact]
        public void TryGetCachedPairedJid_misses_when_cache_empty()
        {
            var store = new LidMappingStore(new InMemoryLidMappingStorage());
            Assert.False(store.TryGetCachedPairedJid("199999999999@lid", out _));
        }
    }
}
