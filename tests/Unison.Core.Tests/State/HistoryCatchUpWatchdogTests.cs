// =============================================================================
// Tests for HistoryCatchUpWatchdog.
//
// An accepted FULL_HISTORY request is watched until it has been quiet for long
// enough. Aborting while lots are still arriving cuts Mobile catch-up short;
// never aborting leaves the request — and the banner — up forever.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class HistoryCatchUpWatchdogTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Poll = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan IdleAbort = TimeSpan.FromSeconds(30);

        private readonly HistoryOnDemandRegistry _registry = new HistoryOnDemandRegistry();
        private readonly List<HistoryCatchUpWatchdogTick> _ticks = new List<HistoryCatchUpWatchdogTick>();
        private DateTime _now = T0;
        private Action _onDelay;

        private HistoryCatchUpWatchdog NewWatchdog() =>
            new HistoryCatchUpWatchdog(_registry, Poll, IdleAbort, () => _now, Delay);

        private Task Delay(TimeSpan span)
        {
            _now = _now.Add(span);
            _onDelay?.Invoke();
            return Task.CompletedTask;
        }

        private HistoryOnDemandRequest StartFull()
        {
            Assert.True(_registry.TryClaimFull());
            return _registry.RegisterFull("F1", true, 100, "offline-stale", T0);
        }

        [Fact]
        public async Task A_quiet_request_is_cleared_once_idle_reaches_the_abort()
        {
            HistoryOnDemandRequest request = StartFull();

            HistoryCatchUpWatchdogExit exit = await NewWatchdog().WatchAsync(request, _ticks.Add);

            Assert.Equal(HistoryCatchUpWatchdogOutcome.Idle, exit.Outcome);
            Assert.Equal(4, _ticks.Count);
            Assert.Equal(TimeSpan.FromSeconds(32), exit.LastTick.Idle);
            Assert.Null(_registry.TrySnapshot(request));
        }

        [Fact]
        public async Task Progress_resets_the_idle_clock()
        {
            HistoryOnDemandRequest request = StartFull();
            int delays = 0;
            _onDelay = () =>
            {
                delays++;
                if (delays == 3)
                {
                    _registry.NoteFullProgress("sqlite-chunk", true, _now, out _, out _);
                }
            };

            HistoryCatchUpWatchdogExit exit = await NewWatchdog().WatchAsync(request, _ticks.Add);

            Assert.Equal(HistoryCatchUpWatchdogOutcome.Idle, exit.Outcome);
            Assert.Equal(7, _ticks.Count);
            Assert.Equal(TimeSpan.Zero, _ticks[2].Idle);
            Assert.Equal(1, exit.LastTick.Request.ProgressSignalCount);
            Assert.Equal(TimeSpan.FromSeconds(56), exit.LastTick.Age);
        }

        [Fact]
        public async Task A_request_cleared_elsewhere_ends_the_watch_without_a_decision()
        {
            HistoryOnDemandRequest request = StartFull();
            _onDelay = () => _registry.TryClear(request);

            HistoryCatchUpWatchdogExit exit = await NewWatchdog().WatchAsync(request, _ticks.Add);

            Assert.Equal(HistoryCatchUpWatchdogOutcome.RequestCleared, exit.Outcome);
            Assert.Empty(_ticks);
        }

        [Fact]
        public async Task A_request_cleared_at_the_abort_tick_is_left_to_whoever_cleared_it()
        {
            HistoryOnDemandRequest request = StartFull();

            HistoryCatchUpWatchdogExit exit = await NewWatchdog().WatchAsync(
                request,
                tick =>
                {
                    if (tick.Idle >= IdleAbort)
                    {
                        _registry.TryClear(request);
                    }
                });

            Assert.Equal(HistoryCatchUpWatchdogOutcome.ClearedAtAbort, exit.Outcome);
        }

        [Fact]
        public async Task Idle_and_age_are_measured_from_when_the_request_was_sent()
        {
            HistoryOnDemandRequest request = StartFull();
            _now = T0.AddSeconds(10);

            await NewWatchdog().WatchAsync(request, _ticks.Add);

            Assert.Equal(TimeSpan.FromSeconds(18), _ticks[0].Idle);
            Assert.Equal(TimeSpan.FromSeconds(18), _ticks[0].Age);
        }
    }
}
