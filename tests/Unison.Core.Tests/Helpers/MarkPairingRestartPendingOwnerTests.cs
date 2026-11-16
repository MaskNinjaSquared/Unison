using System.Collections.Generic;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class MarkPairingRestartPendingOwnerTests
    {
        [Fact]
        public void Run_sets_flag_resets_close_state_and_traces_once()
        {
            bool pending = false;
            bool countCloseAsFatal = true;
            int streak = 3;
            var traces = new List<string>();
            var owner = new MarkPairingRestartPendingOwner(
                () => pending,
                value => pending = value,
                () => countCloseAsFatal = false,
                () => streak = 0,
                traces.Add);

            owner.Run("515");
            owner.Run("515-again");

            Assert.True(pending);
            Assert.False(countCloseAsFatal);
            Assert.Equal(0, streak);
            Assert.Equal(new[] { "pairingRestartPending=true reason=515" }, traces);
        }
    }
}
