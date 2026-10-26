// =============================================================================
// UiStallWatchdog
//
// Diagnostics only. A worker timer posts a Normal-priority probe onto the UI
// thread every second; when it has not run for StallThresholdMs the journal
// gets ui-stall with the dispatcher hop that is executing right now (labelled
// by caller member + file from the IDispatcher implementations), then
// ui-stall-end once the probe lands. Hops slower than SlowWorkThresholdMs are
// written as ui-slow-work even when they never trip a stall.
// =============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Unison.Core.Contracts;
using Windows.UI.Core;

namespace Unison.Uwp.Services
{
    public sealed class UiStallWatchdog
    {
        private const int ProbeIntervalMs = 1000;
        private const int StallThresholdMs = 2000;
        private const int OngoingReportEveryMs = 10000;
        private const int SlowWorkThresholdMs = 300;
        private const string Category = "ui-watchdog";

        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly IAppVisibility _visibility;

        private CoreDispatcher _dispatcher;
        private Timer _timer;

        // Written on the UI thread, read by the probe timer.
        private volatile ActiveWork _active;
        private volatile ActiveWork _lastFinished;

        private long _probeSequence;
        private long _probePostedAt;
        private int _stallReported;
        private long _stallLastReportAt;
        private string _stallFirstWork;

        public UiStallWatchdog(IRuntimeDiagnostics diagnostics, IAppVisibility visibility)
        {
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
        }

        public void Start(CoreDispatcher dispatcher)
        {
            if (dispatcher == null || _timer != null)
            {
                return;
            }

            _dispatcher = dispatcher;
            _timer = new Timer(OnProbeTick, null, ProbeIntervalMs, ProbeIntervalMs);
            _diagnostics.Write(
                Category,
                "started",
                "probeMs=" + ProbeIntervalMs +
                "; stallMs=" + StallThresholdMs +
                "; slowWorkMs=" + SlowWorkThresholdMs);
        }

        /// <summary>UI thread only. Returns the token <see cref="Exit"/> restores.</summary>
        public object Enter(string kind, string caller, string callerFile)
        {
            ActiveWork previous = _active;
            _active = new ActiveWork(kind, caller, callerFile, Stopwatch.GetTimestamp());
            return previous;
        }

        /// <summary>UI thread only.</summary>
        public void Exit(object token)
        {
            ActiveWork finished = _active;
            _active = token as ActiveWork;
            if (finished == null)
            {
                return;
            }

            _lastFinished = finished;
            long elapsedMs = ElapsedMs(finished.StartedAt);
            if (elapsedMs >= SlowWorkThresholdMs && _visibility.IsWindowVisible)
            {
                _diagnostics.Write(
                    Category,
                    "ui-slow-work",
                    "work=" + finished.Describe() + "; elapsedMs=" + elapsedMs);
            }
        }

        public void Run(Action action, string kind, string caller, string callerFile)
        {
            object token = Enter(kind, caller, callerFile);
            try
            {
                action();
            }
            finally
            {
                Exit(token);
            }
        }

        private void OnProbeTick(object state)
        {
            try
            {
                CoreDispatcher dispatcher = _dispatcher;
                if (dispatcher == null)
                {
                    return;
                }

                if (!_visibility.IsWindowVisible)
                {
                    // Suspended / hidden windows do not pump; drop the outstanding probe so the
                    // return to foreground is not reported as a stall.
                    Interlocked.Increment(ref _probeSequence);
                    Interlocked.Exchange(ref _probePostedAt, 0);
                    Interlocked.Exchange(ref _stallReported, 0);
                    return;
                }

                long postedAt = Interlocked.Read(ref _probePostedAt);
                if (postedAt == 0)
                {
                    PostProbe(dispatcher);
                    return;
                }

                long waitedMs = ElapsedMs(postedAt);
                if (waitedMs < StallThresholdMs)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _stallReported, 1, 0) == 0)
                {
                    string work = DescribeActive();
                    _stallFirstWork = work;
                    Interlocked.Exchange(ref _stallLastReportAt, Stopwatch.GetTimestamp());
                    _diagnostics.Write(Category, "ui-stall", "waitedMs=" + waitedMs + "; " + work);
                    _ = _diagnostics.FlushAsync("ui-stall");
                    return;
                }

                if (ElapsedMs(Interlocked.Read(ref _stallLastReportAt)) >= OngoingReportEveryMs)
                {
                    Interlocked.Exchange(ref _stallLastReportAt, Stopwatch.GetTimestamp());
                    _diagnostics.Write(Category, "ui-stall-ongoing", "waitedMs=" + waitedMs + "; " + DescribeActive());
                    _ = _diagnostics.FlushAsync("ui-stall-ongoing");
                }
            }
            catch (Exception ex)
            {
                _diagnostics.RecordException(Category, "probe-tick-failed", ex);
            }
        }

        private void PostProbe(CoreDispatcher dispatcher)
        {
            long sequence = Interlocked.Increment(ref _probeSequence);
            Interlocked.Exchange(ref _probePostedAt, Stopwatch.GetTimestamp());
            _ = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => OnProbeArrived(sequence));
        }

        private void OnProbeArrived(long sequence)
        {
            if (Interlocked.Read(ref _probeSequence) != sequence)
            {
                return;
            }

            long postedAt = Interlocked.Exchange(ref _probePostedAt, 0);
            if (Interlocked.Exchange(ref _stallReported, 0) == 1 && postedAt != 0)
            {
                _diagnostics.Write(
                    Category,
                    "ui-stall-end",
                    "totalMs=" + ElapsedMs(postedAt) + "; first=" + (_stallFirstWork ?? "<none>"));
                _ = _diagnostics.FlushAsync("ui-stall-end");
            }
        }

        private string DescribeActive()
        {
            ActiveWork active = _active;
            ActiveWork last = _lastFinished;
            return
                "active=" + (active != null ? active.Describe() + "; activeMs=" + ElapsedMs(active.StartedAt) : "none") +
                "; lastFinished=" + (last != null ? last.Describe() : "none");
        }

        private static long ElapsedMs(long startedAt)
        {
            if (startedAt == 0)
            {
                return 0;
            }

            return (Stopwatch.GetTimestamp() - startedAt) * 1000L / Stopwatch.Frequency;
        }

        private sealed class ActiveWork
        {
            public ActiveWork(string kind, string caller, string callerFile, long startedAt)
            {
                Kind = kind;
                Caller = caller;
                CallerFile = callerFile;
                StartedAt = startedAt;
            }

            public string Kind { get; }
            public string Caller { get; }
            public string CallerFile { get; }
            public long StartedAt { get; }

            public string Describe()
            {
                string file = string.Empty;
                try
                {
                    file = string.IsNullOrEmpty(CallerFile)
                        ? "?"
                        : Path.GetFileNameWithoutExtension(CallerFile);
                }
                catch
                {
                    file = "?";
                }

                return (Kind ?? "?") + ":" + file + "." + (Caller ?? "?");
            }
        }
    }
}
