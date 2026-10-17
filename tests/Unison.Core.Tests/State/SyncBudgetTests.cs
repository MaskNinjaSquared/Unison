// =============================================================================
// Tests for SyncBudget and InitialSyncProgressState.
//
// When startup and sync work use the frugal budget, and the first-sync
// progress the rule (and the list banner) reads.
// =============================================================================
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class SyncBudgetTests
    {
        private static bool Frugal(
            bool headroom = true,
            bool mobile = false,
            int chats = 0,
            bool firstSync = false,
            bool replay = false,
            bool fullHistory = false,
            DeviceCategory category = DeviceCategory.High) =>
            SyncBudget.PreferFrugal(new SyncBudgetInputs
            {
                HasMemoryHeadroom = headroom,
                IsMobile = mobile,
                ChatCount = chats,
                IsInitialSyncActive = firstSync,
                IsReplayDrainActive = replay,
                IsFullHistoryInFlight = fullHistory,
                DeviceCategory = category
            });

        // --- The rule ----------------------------------------------------------

        [Fact]
        public void An_unsupported_device_is_always_frugal()
        {
            Assert.True(Frugal(headroom: true, mobile: false, category: DeviceCategory.Unsupported));
        }

        [Fact]
        public void Memory_pressure_is_frugal_on_any_device()
        {
            Assert.True(Frugal(headroom: false));
            Assert.True(Frugal(headroom: false, mobile: true));
        }

        [Fact]
        public void A_desktop_with_headroom_stays_generous_whatever_the_sync_is_doing()
        {
            Assert.False(Frugal(chats: 500, firstSync: true, replay: true, fullHistory: true));
        }

        [Fact]
        public void A_phone_with_headroom_and_a_quiet_sync_stays_generous()
        {
            Assert.False(Frugal(mobile: true, chats: 79));
        }

        [Theory]
        [InlineData(80, false, false, false)]
        [InlineData(0, true, false, false)]
        [InlineData(0, false, true, false)]
        [InlineData(0, false, false, true)]
        public void A_phone_goes_frugal_while_the_sync_is_hot(int chats, bool firstSync, bool replay, bool fullHistory)
        {
            Assert.True(Frugal(mobile: true, chats: chats, firstSync: firstSync, replay: replay, fullHistory: fullHistory));
        }

        // --- First-sync progress -----------------------------------------------

        [Fact]
        public void Progress_starts_inactive_and_empty()
        {
            var state = new InitialSyncProgressState();

            Assert.False(state.IsActive);
            Assert.Equal(0, state.ProcessedConversations);
            Assert.Equal(0, state.TotalConversations);
        }

        [Fact]
        public void Publishing_records_the_progress_and_tells_listeners()
        {
            var state = new InitialSyncProgressState();
            InitialSyncProgressEventArgs seen = null;
            state.Changed += (s, e) => seen = e;

            state.Publish(active: true, completed: false, processed: 12, total: 40, stage: "sqlite-chunk");

            Assert.True(state.IsActive);
            Assert.Equal(12, state.ProcessedConversations);
            Assert.Equal(40, state.TotalConversations);
            Assert.NotNull(seen);
            Assert.True(seen.IsActive);
            Assert.False(seen.IsCompleted);
            Assert.Equal(12, seen.ProcessedConversations);
            Assert.Equal(40, seen.TotalConversations);
            Assert.Equal("sqlite-chunk", seen.Stage);
        }

        [Fact]
        public void Negative_counts_are_clamped_to_zero()
        {
            var state = new InitialSyncProgressState();

            state.Publish(active: true, completed: false, processed: -3, total: -1, stage: null);

            Assert.Equal(0, state.ProcessedConversations);
            Assert.Equal(0, state.TotalConversations);
        }

        [Theory]
        [InlineData(5, 100, 20)]
        [InlineData(35, 100, 35)]
        [InlineData(5, 10, 10)]
        public void The_list_releases_at_least_twenty_rows_but_never_more_than_exist(int processed, int total, int target)
        {
            var state = new InitialSyncProgressState();
            InitialSyncProgressEventArgs seen = null;
            state.Changed += (s, e) => seen = e;

            state.Publish(active: true, completed: false, processed: processed, total: total, stage: null);

            Assert.Equal(target, seen.VisibleChatTarget);
        }

        [Fact]
        public void A_listener_that_throws_does_not_stop_the_progress_being_recorded()
        {
            var state = new InitialSyncProgressState();
            state.Changed += (s, e) => throw new System.InvalidOperationException("boom");

            state.Publish(active: false, completed: true, processed: 40, total: 40, stage: "done");

            Assert.False(state.IsActive);
            Assert.Equal(40, state.ProcessedConversations);
        }
    }
}
