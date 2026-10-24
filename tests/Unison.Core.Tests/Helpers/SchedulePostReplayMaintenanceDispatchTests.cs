using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class SchedulePostReplayMaintenanceDispatchTests
    {
        [Fact]
        public void Cancels_previous_and_fires_background()
        {
            var host = new FakeHost();
            SchedulePostReplayMaintenanceDispatch.Run(host, 3);
            Assert.True(host.CancelledPrevious);
            Assert.True(host.CtsReplaced);
            Assert.True(host.FireAndForgetCalled);
        }

        private sealed class FakeHost : ISchedulePostReplayMaintenanceHost
        {
            public bool CancelledPrevious;
            public bool CtsReplaced;
            public bool FireAndForgetCalled;

            public void CancelPreviousPostReplayMaintenanceCts() =>
                CancelledPrevious = true;

            public CancellationTokenSource ReplacePostReplayMaintenanceCts()
            {
                CtsReplaced = true;
                return new CancellationTokenSource();
            }

            public void FireAndForgetPostReplayMaintenance(Func<Task> work) =>
                FireAndForgetCalled = true;

            public Task RunPostReplayMaintenanceAsync(int offlineCount, CancellationToken token) =>
                Task.CompletedTask;

            public void ClearPostReplayMaintenanceCtsIfCurrent(CancellationTokenSource cts)
            {
            }
        }
    }
}
