using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class UserResyncFullHistoryGatesTests
    {
        [Fact]
        public void Needs_fresh_transport_only_when_first_request_fails()
        {
            Assert.True(UserResyncFullHistoryGates.NeedsFreshTransportRetry(false));
            Assert.False(UserResyncFullHistoryGates.NeedsFreshTransportRetry(true));
        }

        [Fact]
        public void Succeeds_after_retry_when_send_or_claim_won()
        {
            Assert.True(UserResyncFullHistoryGates.SucceededAfterRetry(true, false));
            Assert.True(UserResyncFullHistoryGates.SucceededAfterRetry(false, true));
            Assert.False(UserResyncFullHistoryGates.SucceededAfterRetry(false, false));
        }

        [Fact]
        public async Task History_wait_timed_out_when_delay_wins()
        {
            var history = new TaskCompletionSource<bool>();
            Task delay = Task.Delay(1);
            Task finished = await Task.WhenAny(history.Task, delay);

            Assert.True(UserResyncFullHistoryGates.HistoryWaitTimedOut(finished, history.Task));
        }
    }
}
