using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SessionEstablishedGateTests
    {
        [Fact]
        public void A_new_gate_is_pending()
        {
            var gate = new SessionEstablishedGate();

            Assert.False(gate.Task.IsCompleted);
        }

        [Fact]
        public async Task Signal_completes_the_current_task()
        {
            var gate = new SessionEstablishedGate();
            Task pending = gate.Task;

            gate.Signal();

            await pending;
            Assert.True(pending.IsCompleted);
        }

        [Fact]
        public void Renew_hands_out_a_fresh_pending_task_and_leaves_the_old_one_alone()
        {
            var gate = new SessionEstablishedGate();
            Task old = gate.Task;

            gate.Renew();

            Assert.NotSame(old, gate.Task);
            Assert.False(gate.Task.IsCompleted);
            Assert.False(old.IsCompleted);
        }

        [Fact]
        public void Cancel_and_renew_cancels_the_old_waiter()
        {
            var gate = new SessionEstablishedGate();
            Task old = gate.Task;

            gate.CancelAndRenew();

            Assert.True(old.IsCanceled);
            Assert.False(gate.Task.IsCompleted);
        }
    }
}
