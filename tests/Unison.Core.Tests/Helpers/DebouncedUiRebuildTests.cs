using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.Tests.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class DebouncedUiRebuildTests
    {
        [Fact]
        public async Task Burst_of_Schedule_coalesces_into_one_run_after_delay()
        {
            var dispatcher = new CountingDispatcher();
            int runs = 0;
            bool alive = true;
            var debounced = new DebouncedUiRebuild(
                () => alive,
                () => runs++,
                dispatcher,
                TimeSpan.FromMilliseconds(50),
                "test");

            for (int i = 0; i < 20; i++)
            {
                debounced.Schedule();
            }

            Assert.Equal(0, runs);
            Assert.Equal(0, dispatcher.Passes);

            Assert.True(SpinWait.SpinUntil(() => runs >= 1, TimeSpan.FromSeconds(2)));
            await Task.Delay(100).ConfigureAwait(true);

            Assert.Equal(1, runs);
            Assert.Equal(1, dispatcher.Passes);
        }

        [Fact]
        public async Task Cancel_and_dead_canRun_skip_the_scheduled_run()
        {
            var dispatcher = new CountingDispatcher();
            int runs = 0;
            bool alive = true;
            var debounced = new DebouncedUiRebuild(
                () => alive,
                () => runs++,
                dispatcher,
                TimeSpan.FromMilliseconds(80),
                "test");

            debounced.Schedule();
            alive = false;
            debounced.Cancel();

            await Task.Delay(200).ConfigureAwait(true);

            Assert.Equal(0, runs);
            Assert.Equal(0, dispatcher.Passes);
        }
    }
}
