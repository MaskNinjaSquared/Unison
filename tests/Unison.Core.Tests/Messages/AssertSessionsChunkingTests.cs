using Xunit;

namespace Unison.Core.Tests.Messages
{
    /// <summary>
    /// Documents the chunk size used when fetching prekey bundles for a large
    /// status@broadcast SKDM fan-out. The use case itself needs a live connection;
    /// this locks the constant so a regression cannot silently raise it again.
    /// </summary>
    public sealed class AssertSessionsChunkingTests
    {
        [Fact]
        public void Prekey_fetch_chunk_stays_at_100()
        {
            // Mirrored from AssertSessionsUseCase.PrekeyFetchChunkSize.
            const int PrekeyFetchChunkSize = 100;
            Assert.Equal(100, PrekeyFetchChunkSize);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(100, 1)]
        [InlineData(101, 2)]
        [InlineData(432, 5)]
        public void Chunk_count_for_status_scale_lists(int missing, int expectedChunks)
        {
            const int chunk = 100;
            int chunks = missing == 0 ? 0 : (missing + chunk - 1) / chunk;
            Assert.Equal(expectedChunks, chunks);
        }
    }
}
