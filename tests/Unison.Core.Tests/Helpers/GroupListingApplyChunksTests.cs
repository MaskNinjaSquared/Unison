using Unison.Core.Helpers.Groups;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupListingApplyChunksTests
    {
        [Fact]
        public void Frugal_chunks_are_smaller_than_desktop()
        {
            Assert.True(
                GroupListingApplyChunks.SubjectCacheChunk(true) <
                GroupListingApplyChunks.SubjectCacheChunk(false));
            Assert.True(
                GroupListingApplyChunks.HubEnsureChunk(true) <
                GroupListingApplyChunks.HubEnsureChunk(false));
            Assert.True(
                GroupListingApplyChunks.InterChunkDelay(true) >
                GroupListingApplyChunks.InterChunkDelay(false));
        }

        [Fact]
        public void Flag_paint_chunk_matches_existing_listing_budget()
        {
            Assert.Equal(24, GroupListingApplyChunks.FlagPaintChunk);
        }
    }
}
