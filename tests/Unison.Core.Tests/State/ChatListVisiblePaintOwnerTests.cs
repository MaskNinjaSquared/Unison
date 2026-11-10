// =============================================================================
// Tests for ChatListVisiblePaintOwner — one Transient list VM paints at a time.
// =============================================================================
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class ChatListVisiblePaintOwnerTests
    {
        [Fact]
        public void Newer_claim_invalidates_the_previous_owner()
        {
            var first = new object();
            var second = new object();

            int gen1 = ChatListVisiblePaintOwner.Claim(first);
            Assert.True(ChatListVisiblePaintOwner.IsCurrent(first, gen1));

            int gen2 = ChatListVisiblePaintOwner.Claim(second);
            Assert.False(ChatListVisiblePaintOwner.IsCurrent(first, gen1));
            Assert.True(ChatListVisiblePaintOwner.IsCurrent(second, gen2));
        }

        [Fact]
        public void Release_only_clears_matching_owner_generation()
        {
            var first = new object();
            var second = new object();

            int gen1 = ChatListVisiblePaintOwner.Claim(first);
            int gen2 = ChatListVisiblePaintOwner.Claim(second);

            ChatListVisiblePaintOwner.Release(first, gen1);
            Assert.True(ChatListVisiblePaintOwner.IsCurrent(second, gen2));

            ChatListVisiblePaintOwner.Release(second, gen2);
            Assert.False(ChatListVisiblePaintOwner.IsCurrent(second, gen2));
        }
    }
}
