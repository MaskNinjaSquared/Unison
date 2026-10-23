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

            await reader.WarmCacheFromStorageAsync();

            Assert.True(reader.TryGetCachedPairedJid("199999999999@lid", out string pn));
            Assert.Equal("5511999990000@s.whatsapp.net", pn);
        }
    }
}
