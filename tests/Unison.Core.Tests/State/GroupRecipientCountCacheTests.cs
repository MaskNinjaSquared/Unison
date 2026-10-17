// =============================================================================
// Tests for GroupRecipientCountCache.
//
// How many members a group receipt has to hear from, remembered for half an
// hour so every check mark does not cost a metadata query.
// =============================================================================
using System;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class GroupRecipientCountCacheTests
    {
        private const string Group = "120363000000000001@g.us";

        private static readonly DateTime Now = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Nothing_stored_answers_nothing()
        {
            var cache = new GroupRecipientCountCache();

            Assert.False(cache.TryGetFresh(Group, Now, out int count));
            Assert.Equal(0, count);
        }

        [Fact]
        public void A_count_stored_moments_ago_is_answered()
        {
            var cache = new GroupRecipientCountCache();
            cache.Store(Group, 7, Now);

            Assert.True(cache.TryGetFresh(Group, Now.AddMinutes(29), out int count));
            Assert.Equal(7, count);
        }

        [Fact]
        public void A_count_half_an_hour_old_is_stale()
        {
            var cache = new GroupRecipientCountCache();
            cache.Store(Group, 7, Now);

            Assert.False(cache.TryGetFresh(Group, Now + GroupRecipientCountCache.FreshFor, out _));
        }

        [Fact]
        public void The_group_address_is_matched_without_regard_to_case()
        {
            var cache = new GroupRecipientCountCache();
            cache.Store(Group, 3, Now);

            Assert.True(cache.TryGetFresh(Group.ToUpperInvariant(), Now, out int count));
            Assert.Equal(3, count);
        }

        [Fact]
        public void A_new_count_replaces_the_old_one_and_restarts_the_clock()
        {
            var cache = new GroupRecipientCountCache();
            cache.Store(Group, 3, Now);
            cache.Store(Group, 5, Now.AddMinutes(20));

            Assert.True(cache.TryGetFresh(Group, Now.AddMinutes(45), out int count));
            Assert.Equal(5, count);
        }

        [Fact]
        public void Groups_do_not_share_a_count()
        {
            var cache = new GroupRecipientCountCache();
            cache.Store(Group, 3, Now);

            Assert.False(cache.TryGetFresh("120363000000000002@g.us", Now, out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_blank_address_is_neither_stored_nor_answered(string blank)
        {
            var cache = new GroupRecipientCountCache();
            cache.Store(blank, 4, Now);

            Assert.False(cache.TryGetFresh(blank, Now, out _));
        }
    }
}
