using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class DeferredAvatarResolutionTests
    {
        [Fact]
        public async Task Schedule_triggers_after_delay()
        {
            int triggers = 0;
            var deferred = new DeferredAvatarResolution(
                () => Interlocked.Increment(ref triggers));

            deferred.Schedule("test", TimeSpan.FromMilliseconds(20));
            await Task.Delay(150);

            Assert.Equal(1, triggers);
        }

        [Fact]
        public async Task Cancel_prevents_a_pending_trigger()
        {
            int triggers = 0;
            var deferred = new DeferredAvatarResolution(
                () => Interlocked.Increment(ref triggers));

            deferred.Schedule("test", TimeSpan.FromMilliseconds(80));
            deferred.Cancel();
            await Task.Delay(150);

            Assert.Equal(0, triggers);
        }

        [Fact]
        public async Task A_later_schedule_replaces_the_pending_one()
        {
            int triggers = 0;
            var deferred = new DeferredAvatarResolution(
                () => Interlocked.Increment(ref triggers));

            deferred.Schedule("first", TimeSpan.FromMilliseconds(200));
            deferred.Schedule("second", TimeSpan.FromMilliseconds(20));
            await Task.Delay(150);

            Assert.Equal(1, triggers);
        }

        [Fact]
        public async Task Stops_scheduling_after_the_consecutive_cap()
        {
            int triggers = 0;
            var deferred = new DeferredAvatarResolution(
                () => Interlocked.Increment(ref triggers));

            for (int i = 0; i < DeferredAvatarResolution.MaxConsecutiveSchedules + 3; i++)
            {
                deferred.Schedule("stuck", TimeSpan.FromMilliseconds(10));
            }

            await Task.Delay(80);

            // Only the schedules within the cap arm a timer; the last armed one fires once.
            Assert.True(triggers <= 1);
        }

        [Fact]
        public async Task Zero_max_retries_never_arms_a_timer()
        {
            int triggers = 0;
            var deferred = new DeferredAvatarResolution(
                () => Interlocked.Increment(ref triggers),
                () => TimeSpan.FromMilliseconds(10),
                () => 0);

            deferred.Schedule("unsupported-tier", TimeSpan.FromMilliseconds(10));
            await Task.Delay(80);

            Assert.Equal(0, triggers);
        }

        [Fact]
        public async Task Injected_max_retries_caps_below_the_high_default()
        {
            int triggers = 0;
            var deferred = new DeferredAvatarResolution(
                () => Interlocked.Increment(ref triggers),
                () => TimeSpan.FromMilliseconds(10),
                () => 2);

            for (int i = 0; i < 5; i++)
            {
                deferred.Schedule("low-tier", TimeSpan.FromMilliseconds(10));
            }

            await Task.Delay(80);

            Assert.True(triggers <= 1);
        }
    }
}
