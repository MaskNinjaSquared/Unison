using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SilentFullHistoryChainDispatchTests
    {
        [Fact]
        public void Productive_lot_resets_empty_streak_and_schedules()
        {
            var host = new FakeHost
            {
                Armed = true,
                EmptyStreak = 3
            };

            SilentFullHistoryChainDispatch.OnLotQuiet(
                host,
                SilentFullHistoryPolicy.ReasonPrefix + "next",
                appliedCount: 5);

            Assert.Equal(0, host.EmptyStreak);
            Assert.True(host.BannerBegun);
            Assert.True(host.ScheduleStarted);
            Assert.True(host.Armed);
        }

        [Fact]
        public void Fourth_empty_lot_stops_chain_with_empty_cap()
        {
            var host = new FakeHost
            {
                Armed = true,
                EmptyStreak = 3
            };

            SilentFullHistoryChainDispatch.OnLotQuiet(
                host,
                SilentFullHistoryPolicy.ReasonPrefix + "next",
                appliedCount: 0);

            Assert.Equal(4, host.EmptyStreak);
            Assert.False(host.Armed);
            Assert.Equal("empty-cap", host.LastStopReason);
            Assert.True(host.BannerEnded);
            Assert.False(host.ScheduleStarted);
        }

        [Fact]
        public void Disarmed_productive_lot_still_raises_banner_without_reschedule()
        {
            var host = new FakeHost
            {
                Armed = false,
                EmptyStreak = 4
            };

            SilentFullHistoryChainDispatch.OnLotQuiet(
                host,
                SilentFullHistoryPolicy.ReasonPrefix + "residual",
                appliedCount: 2);

            Assert.True(host.BannerBegun);
            Assert.False(host.ScheduleStarted);
            Assert.False(host.Armed);
            Assert.Null(host.LastStopReason);
        }

        [Fact]
        public void Arm_resets_empty_streak()
        {
            var host = new FakeHost
            {
                EmptyStreak = 2
            };

            SilentFullHistoryChainDispatch.Arm(host, "post-init");

            Assert.Equal(0, host.EmptyStreak);
            Assert.True(host.Armed);
            Assert.True(host.ScheduleStarted);
        }

        private sealed class FakeHost : ISilentFullHistoryChainHost
        {
            public bool Armed { get; set; }
            public bool PreviousQuiet { get; set; } = true;
            public int EmptyStreak { get; set; }
            public bool BannerBegun { get; private set; }
            public bool BannerEnded { get; private set; }
            public bool ScheduleStarted { get; private set; }
            public string LastStopReason { get; private set; }

            public bool IsSilentChainArmed => Armed;

            public bool IsPreviousSilentLotQuiet => PreviousQuiet;

            public int ConsecutiveEmptySilentLots => EmptyStreak;

            public bool IsSocketReadyForSilentFull => true;

            public bool IsHistoryCatchUpActive => false;

            public bool IsFullHistoryClaimed => false;

            public DeviceCategory DeviceCategory => DeviceCategory.High;

            public void MarkSilentChainArmed(bool armed) => Armed = armed;

            public void MarkPreviousSilentLotQuiet(bool quiet) => PreviousQuiet = quiet;

            public void MarkConsecutiveEmptySilentLots(int count) => EmptyStreak = count;

            public void CancelSilentSchedule()
            {
            }

            public CancellationTokenSource CreateSilentScheduleCts() => new CancellationTokenSource();

            public void StartSilentScheduleTask(Func<Task> work) => ScheduleStarted = true;

            public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.CompletedTask;

            public Task<bool> RequestSilentFullHistoryAsync(string reason) => Task.FromResult(true);

            public void BeginHistoryCatchUpBanner(string reason) => BannerBegun = true;

            public void EndHistoryCatchUpBanner(string reason) => BannerEnded = true;

            public void WriteSilentJournal(string eventName, string detail)
            {
                if (string.Equals(eventName, "stop", StringComparison.Ordinal))
                {
                    LastStopReason = detail;
                }
            }

            public void LogSilentChainFailed(string message)
            {
            }
        }
    }
}
