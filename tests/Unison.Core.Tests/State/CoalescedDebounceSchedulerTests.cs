using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class CoalescedDebounceSchedulerTests
    {
        [Fact]
        public async Task A_single_arm_runs_the_callback_once()
        {
            int runs = 0;
            using (var scheduler = new CoalescedDebounceScheduler(TimeSpan.FromMilliseconds(40)))
            {
                scheduler.Schedule(() => Interlocked.Increment(ref runs));
                await Task.Delay(120).ConfigureAwait(false);
            }

            Assert.Equal(1, runs);
        }

        [Fact]
        public async Task A_burst_coalesces_into_one_callback()
        {
            int runs = 0;
            using (var scheduler = new CoalescedDebounceScheduler(TimeSpan.FromMilliseconds(60)))
            {
                scheduler.Schedule(() => Interlocked.Increment(ref runs));
                await Task.Delay(20).ConfigureAwait(false);
                scheduler.Schedule(() => Interlocked.Increment(ref runs));
                await Task.Delay(20).ConfigureAwait(false);
                scheduler.Schedule(() => Interlocked.Increment(ref runs));
                await Task.Delay(150).ConfigureAwait(false);
            }

            Assert.Equal(1, runs);
        }

        [Fact]
        public async Task Cancel_drops_a_pending_arm()
        {
            int runs = 0;
            using (var scheduler = new CoalescedDebounceScheduler(TimeSpan.FromMilliseconds(80)))
            {
                scheduler.Schedule(() => Interlocked.Increment(ref runs));
                scheduler.Cancel();
                await Task.Delay(150).ConfigureAwait(false);
            }

            Assert.Equal(0, runs);
        }

        [Fact]
        public async Task Dispose_drops_a_pending_arm()
        {
            int runs = 0;
            var scheduler = new CoalescedDebounceScheduler(TimeSpan.FromMilliseconds(80));
            scheduler.Schedule(() => Interlocked.Increment(ref runs));
            scheduler.Dispose();
            await Task.Delay(150).ConfigureAwait(false);

            Assert.Equal(0, runs);
        }
    }
}
