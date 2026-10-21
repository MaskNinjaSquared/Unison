using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GifPlaybackCoordinatorTests
    {
        private sealed class FakeClient : IGifPlaybackClient
        {
            public int PauseCount { get; private set; }

            public void PauseFromCoordinator()
            {
                PauseCount++;
            }
        }

        [Fact]
        public void Unsupported_pauses_others_when_one_plays()
        {
            var coordinator = new GifPlaybackCoordinator(DeviceCategory.Unsupported);
            var a = new FakeClient();
            var b = new FakeClient();
            coordinator.Register(a);
            coordinator.Register(b);

            coordinator.NotifyPlaying(a);

            Assert.Equal(0, a.PauseCount);
            Assert.Equal(1, b.PauseCount);
        }

        [Fact]
        public void Low_early_returns_without_pausing()
        {
            var coordinator = new GifPlaybackCoordinator(DeviceCategory.Low);
            var a = new FakeClient();
            var b = new FakeClient();
            coordinator.Register(a);
            coordinator.Register(b);

            coordinator.NotifyPlaying(a);

            Assert.Equal(0, a.PauseCount);
            Assert.Equal(0, b.PauseCount);
        }

        [Fact]
        public void Unregister_stops_receiving_pause()
        {
            var coordinator = new GifPlaybackCoordinator(DeviceCategory.Unsupported);
            var a = new FakeClient();
            var b = new FakeClient();
            coordinator.Register(a);
            coordinator.Register(b);
            coordinator.Unregister(b);

            coordinator.NotifyPlaying(a);

            Assert.Equal(0, b.PauseCount);
        }
    }
}
