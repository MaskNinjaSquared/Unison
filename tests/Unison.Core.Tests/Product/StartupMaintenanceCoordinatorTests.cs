using Unison.Core.Contracts.Tuning;
using Unison.Core.Models;
using Unison.Core.Product;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Product
{
    public sealed class StartupMaintenanceCoordinatorTests
    {
        [Fact]
        public void Continues_when_frugal_requirements_are_off()
        {
            StartupMaintenanceCoordinator coordinator = Create(frugal: false);

            Assert.Equal(
                DeferredStartupGateAction.Continue,
                coordinator.DecideGate(windowVisible: false));
        }

        [Fact]
        public void Skips_when_frugal_and_window_hidden()
        {
            StartupMaintenanceCoordinator coordinator = Create(frugal: true);

            Assert.Equal(
                DeferredStartupGateAction.SkipVisibility,
                coordinator.DecideGate(windowVisible: false));
        }

        [Fact]
        public void Checks_memory_when_frugal_and_window_visible()
        {
            StartupMaintenanceCoordinator coordinator = Create(frugal: true);

            Assert.Equal(
                DeferredStartupGateAction.CheckMemory,
                coordinator.DecideGate(windowVisible: true));
        }

        [Theory]
        [InlineData(0, false, false)]
        [InlineData(3, true, false)]
        [InlineData(3, false, true)]
        [InlineData(3, false, false)]
        public void Resolve_missing_names_uses_tuning_and_catch_up(
            int chats,
            bool frugal,
            bool historyCatchUpActive)
        {
            StartupMaintenanceCoordinator coordinator = Create(frugal);
            bool expected = chats > 0 && !frugal && !historyCatchUpActive;

            Assert.Equal(
                expected,
                coordinator.ShouldResolveMissingNames(chats, historyCatchUpActive));
        }

        private static StartupMaintenanceCoordinator Create(bool frugal)
        {
            bool live = frugal;
            IStartupMaintenanceTuning tuning = new SyncProductPolicy(
                SyncWorkloadStrategy.For(DeviceCategory.Low),
                () => live);
            return new StartupMaintenanceCoordinator(tuning);
        }
    }
}
