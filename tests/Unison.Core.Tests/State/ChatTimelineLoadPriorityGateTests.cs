using System.Threading.Tasks;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class ChatTimelineLoadPriorityGateTests
    {
        [Fact]
        public void Nested_holds_stay_held_until_matching_releases()
        {
            var gate = new ChatTimelineLoadPriorityGate();
            Assert.False(gate.IsHeld);

            gate.Hold("prepare");
            Assert.True(gate.IsHeld);

            gate.Hold("complete");
            Assert.True(gate.IsHeld);

            gate.Release("complete");
            Assert.True(gate.IsHeld);

            gate.Release("prepare");
            Assert.False(gate.IsHeld);
        }

        [Fact]
        public void Extra_release_clamps_to_not_held()
        {
            var gate = new ChatTimelineLoadPriorityGate();
            gate.Release("spurious");
            Assert.False(gate.IsHeld);

            gate.Hold("one");
            gate.Release("one");
            gate.Release("extra");
            Assert.False(gate.IsHeld);
        }

        [Fact]
        public async Task WaitWhileHeldAsync_returns_after_release()
        {
            var gate = new ChatTimelineLoadPriorityGate();
            gate.Hold("open");

            Task wait = gate.WaitWhileHeldAsync();
            await Task.Delay(80);
            Assert.False(wait.IsCompleted);

            gate.Release("open");
            await wait;
            Assert.False(gate.IsHeld);
        }
    }
}
