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
    }
}
