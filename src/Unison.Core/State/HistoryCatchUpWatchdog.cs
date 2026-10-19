// =============================================================================
// HistoryCatchUpWatchdog
//
// Watches an accepted FULL_HISTORY request and clears it only after a stretch
// with no catch-up progress (tips, SQLite chunks, offline apply). Progress
// resets the idle clock; Mobile often needs several minutes of slow delivery.
//
// The watch ends quietly when something else clears the request first. What to
// do with an idle lot — ask again or finish — stays with the caller.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.Core.State
{
    public enum HistoryCatchUpWatchdogOutcome
    {
        /// <summary>The request was answered or cleared by someone else between polls.</summary>
        RequestCleared,

        /// <summary>Idle long enough, but someone else cleared it first; theirs is the decision.</summary>
        ClearedAtAbort,

        /// <summary>Quiet for the abort window; this watch cleared the request.</summary>
        Idle
    }

    public struct HistoryCatchUpWatchdogTick
    {
        /// <summary>A copy of the request as it stood at this tick.</summary>
        public HistoryOnDemandRequest Request;

        public TimeSpan Idle;

        public TimeSpan Age;
    }

    public struct HistoryCatchUpWatchdogExit
    {
        public HistoryCatchUpWatchdogOutcome Outcome;

        /// <summary>The tick that ended the watch; empty when the request was cleared.</summary>
        public HistoryCatchUpWatchdogTick LastTick;
    }

    public sealed class HistoryCatchUpWatchdog
    {
        private readonly HistoryOnDemandRegistry _registry;
        private readonly TimeSpan _poll;
        private readonly TimeSpan _idleAbort;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<TimeSpan, Task> _delay;

        public HistoryCatchUpWatchdog(
            HistoryOnDemandRegistry registry,
            TimeSpan poll,
            TimeSpan idleAbort,
            Func<DateTime> utcNow = null,
            Func<TimeSpan, Task> delay = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _poll = poll;
            _idleAbort = idleAbort;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _delay = delay ?? Task.Delay;
        }

        public TimeSpan Poll => _poll;

        public TimeSpan IdleAbort => _idleAbort;

        /// <param name="onTick">Called on every poll while the request is still current, before the idle check.</param>
        public async Task<HistoryCatchUpWatchdogExit> WatchAsync(
            HistoryOnDemandRequest request,
            Action<HistoryCatchUpWatchdogTick> onTick)
        {
            while (true)
            {
                await _delay(_poll).ConfigureAwait(false);

                HistoryOnDemandRequest pending = _registry.TrySnapshot(request);
                if (pending == null)
                {
                    return new HistoryCatchUpWatchdogExit { Outcome = HistoryCatchUpWatchdogOutcome.RequestCleared };
                }

                HistoryCatchUpWatchdogTick tick = Measure(pending, _utcNow());
                onTick?.Invoke(tick);

                if (tick.Idle < _idleAbort)
                {
                    continue;
                }

                if (!_registry.TryClear(request))
                {
                    return new HistoryCatchUpWatchdogExit
                    {
                        Outcome = HistoryCatchUpWatchdogOutcome.ClearedAtAbort,
                        LastTick = tick
                    };
                }

                return new HistoryCatchUpWatchdogExit
                {
                    Outcome = HistoryCatchUpWatchdogOutcome.Idle,
                    LastTick = tick
                };
            }
        }

        private static HistoryCatchUpWatchdogTick Measure(HistoryOnDemandRequest pending, DateTime nowUtc)
        {
            DateTime requestedAtUtc = pending.RequestedAtUtc != DateTime.MinValue
                ? pending.RequestedAtUtc
                : nowUtc;
            DateTime lastProgressUtc = pending.LastProgressUtc != DateTime.MinValue
                ? pending.LastProgressUtc
                : requestedAtUtc;

            return new HistoryCatchUpWatchdogTick
            {
                Request = pending,
                Idle = nowUtc - lastProgressUtc,
                Age = nowUtc - requestedAtUtc
            };
        }
    }
}
