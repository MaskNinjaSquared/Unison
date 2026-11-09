using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class ChatTimelineLoadPriorityGateTests
    {
        [Fact]
        public async Task WaitWhileHeldAsync_staggers_waiters_after_release()
        {
            var gate = new ChatTimelineLoadPriorityGate();
            gate.Hold("test");

            Task first = gate.WaitWhileHeldAsync();
            Task second = gate.WaitWhileHeldAsync();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);

            gate.Release("test");

            var sw = Stopwatch.StartNew();
            await Task.WhenAll(first, second);
            sw.Stop();

            // Two admits with PostReleaseAdmitGapMs between them — must not finish in one
            // instantaneous stampede (unload + ApplyMetadata fought that way on Mobile).
            Assert.True(
                sw.ElapsedMilliseconds >= ChatTimelineLoadPriorityGate.PostReleaseAdmitGapMs,
                "elapsedMs=" + sw.ElapsedMilliseconds);
        }

        [Fact]
        public async Task WaitWhileHeldAsync_returns_immediately_when_not_held()
        {
            var gate = new ChatTimelineLoadPriorityGate();
            var sw = Stopwatch.StartNew();
            await gate.WaitWhileHeldAsync();
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 200);
        }

        [Fact]
        public async Task WaitWhileHeldAsync_times_out_while_still_held()
        {
            var gate = new ChatTimelineLoadPriorityGate();
            gate.Hold("test-timeout");

            var sw = Stopwatch.StartNew();
            await gate.WaitWhileHeldAsync(TimeSpan.FromMilliseconds(120));
            sw.Stop();

            Assert.True(gate.IsHeld);
            Assert.True(sw.ElapsedMilliseconds >= 100);
            Assert.True(sw.ElapsedMilliseconds < 2000);

            gate.Release("test-timeout");
        }
    }
}
