using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class StartBackgroundHistoryBackfillDispatchTests
    {
        [Fact]
        public async Task Lock_busy_returns_zero()
        {
            var host = new FakeHost { Acquire = false };
            int n = await StartBackgroundHistoryBackfillDispatch.RunAsync(host, "t");
            Assert.Equal(0, n);
            Assert.True(host.LoggedSkip);
        }

        [Fact]
        public async Task Runs_core_and_releases_lock()
        {
            var host = new FakeHost { Acquire = true, CoreResult = 2 };
            int n = await StartBackgroundHistoryBackfillDispatch.RunAsync(host, "go");
            Assert.Equal(2, n);
            Assert.Equal(1, host.ActiveSet);
            Assert.Equal(0, host.ActiveCleared);
            Assert.True(host.Released);
        }

        private sealed class FakeHost : IStartBackgroundHistoryBackfillHost
        {
            public bool Acquire;
            public int CoreResult;
            public bool LoggedSkip;
            public int ActiveSet = -1;
            public int ActiveCleared = -1;
            public bool Released;

            public Task<bool> TryAcquireHistoryHydrationAsync() =>
                Task.FromResult(Acquire);

            public void ReleaseHistoryHydration() => Released = true;

            public void SetHistoryHydrationActive(int value)
            {
                if (value == 1)
                {
                    ActiveSet = value;
                }
                else
                {
                    ActiveCleared = value;
                }
            }

            public void LogHistoryHydrationSkippedAlreadyRunning(string reason) =>
                LoggedSkip = true;

            public Task<int> RunHistoryHydrationBackfillCoreAsync(string reason) =>
                Task.FromResult(CoreResult);
        }
    }
}
