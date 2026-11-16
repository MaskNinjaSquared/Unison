using System;
using Unison.Core.Contracts.Tuning;
using Unison.Core.Models;
using Unison.Core.Product;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Product
{
    public sealed class PostReplayMaintenanceCoordinatorTests
    {
        [Fact]
        public void Aborts_when_quiet_connected_or_visible_fails()
        {
            Assert.True(PostReplayMaintenanceCoordinator.ShouldAbortBeforeWork(false, true, true));
            Assert.True(PostReplayMaintenanceCoordinator.ShouldAbortBeforeWork(true, false, true));
            Assert.True(PostReplayMaintenanceCoordinator.ShouldAbortBeforeWork(true, true, false));
            Assert.False(PostReplayMaintenanceCoordinator.ShouldAbortBeforeWork(true, true, true));
        }

        [Fact]
        public void Quiet_wait_and_delayed_bulk_follow_tuning()
        {
            bool frugal = false;
            IStartupMaintenanceTuning tuning = new SyncProductPolicy(
                SyncWorkloadStrategy.For(DeviceCategory.High),
                () => frugal);
            var coordinator = new PostReplayMaintenanceCoordinator(tuning);

            Assert.Equal(TimeSpan.FromSeconds(12), coordinator.QuietWait);
            Assert.True(coordinator.RunDelayedBulkAfterReplay(50));
            Assert.False(coordinator.RunDelayedBulkAfterReplay(49));

            frugal = true;
            Assert.Equal(TimeSpan.FromSeconds(8), coordinator.QuietWait);
            Assert.False(coordinator.RunDelayedBulkAfterReplay(50));
        }
    }
}
