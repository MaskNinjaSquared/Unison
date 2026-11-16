using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListReleaseBudgetPolicyTests
    {
        [Fact]
        public void First_batch_is_unconditional_even_over_budget()
        {
            Assert.True(ChatListReleaseBudgetPolicy.CanAddMore(added: 7, batchSize: 8, addElapsedMs: 50, budgetMs: 8));
        }

        [Fact]
        public void Past_first_batch_keeps_going_while_add_time_is_under_budget()
        {
            Assert.True(ChatListReleaseBudgetPolicy.CanAddMore(added: 40, batchSize: 8, addElapsedMs: 5, budgetMs: 8));
        }

        [Fact]
        public void Past_first_batch_stops_once_add_time_reaches_budget()
        {
            Assert.False(ChatListReleaseBudgetPolicy.CanAddMore(added: 8, batchSize: 8, addElapsedMs: 8, budgetMs: 8));
        }

        [Fact]
        public void Add_clock_is_not_started_by_rows_skipped_before_the_first_add()
        {
            // b31 fb8e90ad: skipping 400 visible rows took ~90ms and capped every tick at 8 rows.
            Assert.Equal(0, ChatListReleaseBudgetPolicy.AddElapsedMs(addStartedTicks: 0, nowTicks: 900_000, ticksPerMs: 10_000));
            Assert.Equal(3, ChatListReleaseBudgetPolicy.AddElapsedMs(addStartedTicks: 870_000, nowTicks: 900_000, ticksPerMs: 10_000));
        }
    }
}
