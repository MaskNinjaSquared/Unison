// =============================================================================
// Tests for PersistScheduler.
//
// The debounce that decides when chat rows reach disk. The startup-suppression
// half of it was the part with no lock and no way to exercise it short of
// launching the app and watching a log line.
// =============================================================================
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class PersistSchedulerTests
    {
        /// <summary>A scheduler past startup, which is the steady state.</summary>
        private static PersistScheduler Running()
        {
            var scheduler = new PersistScheduler();
            scheduler.EnableAfterStartup();
            return scheduler;
        }

        // --- startup suppression ---------------------------------------------

        [Fact]
        public void A_fresh_scheduler_is_suppressed_for_startup()
        {
            var scheduler = new PersistScheduler();

            Assert.True(scheduler.IsSuppressedForStartup);
            Assert.Equal(PersistScheduleAction.Deferred, scheduler.Request());
        }

        [Fact]
        public void A_save_asked_for_during_startup_is_remembered_not_dropped()
        {
            var scheduler = new PersistScheduler();

            scheduler.Request();

            Assert.True(scheduler.IsPending);
        }

        [Fact]
        public void Lifting_suppression_settles_a_deferred_save()
        {
            var scheduler = new PersistScheduler();
            scheduler.Request();

            Assert.Equal(PersistEnableResult.LiftedWithDeferredSave, scheduler.EnableAfterStartup());
        }

        [Fact]
        public void Lifting_suppression_with_nothing_owed_says_so()
        {
            var scheduler = new PersistScheduler();

            Assert.Equal(PersistEnableResult.Lifted, scheduler.EnableAfterStartup());
        }

        [Fact]
        public void Lifting_suppression_twice_settles_the_debt_once()
        {
            // Two callers racing to lift startup suppression must not both decide they
            // owe the deferred save.
            var scheduler = new PersistScheduler();
            scheduler.Request();
            scheduler.EnableAfterStartup();

            Assert.Equal(PersistEnableResult.AlreadyEnabled, scheduler.EnableAfterStartup());
        }

        [Fact]
        public void After_startup_a_request_arms_the_timer()
        {
            Assert.Equal(PersistScheduleAction.ArmTimer, Running().Request());
        }

        // --- the debounce ----------------------------------------------------

        [Fact]
        public void The_elapsed_debounce_runs_the_save_that_was_asked_for()
        {
            var scheduler = Running();
            scheduler.Request();

            Assert.True(scheduler.TryBeginPersist());
        }

        [Fact]
        public void A_debounce_that_elapses_with_nothing_owed_does_not_save()
        {
            Assert.False(Running().TryBeginPersist());
        }

        [Fact]
        public void Only_one_of_two_elapsed_timers_runs_the_save()
        {
            // Restarting the debounce leaves the old timer alive until it is disposed,
            // so both can fire. The flag is what makes the second one a no-op.
            var scheduler = Running();
            scheduler.Request();

            Assert.True(scheduler.TryBeginPersist());
            Assert.False(scheduler.TryBeginPersist());
        }

        [Fact]
        public void Many_requests_within_one_window_collapse_into_one_save()
        {
            var scheduler = Running();
            for (int i = 0; i < 50; i++)
            {
                scheduler.Request();
            }

            Assert.True(scheduler.TryBeginPersist());
            Assert.False(scheduler.TryBeginPersist());
        }

        [Fact]
        public void A_request_after_the_save_started_is_not_swallowed()
        {
            // Otherwise a change made while the write was in flight would never land.
            var scheduler = Running();
            scheduler.Request();
            scheduler.TryBeginPersist();

            scheduler.Request();

            Assert.True(scheduler.IsPending);
            Assert.True(scheduler.TryBeginPersist());
        }

        // --- shutdown --------------------------------------------------------

        [Fact]
        public void Shutdown_forgets_the_owed_save()
        {
            var scheduler = Running();
            scheduler.Request();

            scheduler.Reset();

            Assert.False(scheduler.IsPending);
            Assert.False(scheduler.TryBeginPersist());
        }

        [Fact]
        public void Shutdown_does_not_put_startup_suppression_back()
        {
            var scheduler = Running();

            scheduler.Reset();

            Assert.Equal(PersistScheduleAction.ArmTimer, scheduler.Request());
        }
    }
}
