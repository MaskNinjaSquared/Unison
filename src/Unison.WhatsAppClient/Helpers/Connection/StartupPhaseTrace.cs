// =============================================================================
// StartupPhaseTrace
//
// Brackets a startup phase in the runtime journal with duration and memory
// level at both ends. Wall-clock can span suspend; activeMs excludes time
// while the window is hidden so journals do not look like multi-minute hangs.
// =============================================================================
using System;
using System.Diagnostics;
using Unison.Core.Contracts;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Brackets a startup phase in the runtime journal with its duration and the memory level
    /// at both ends, so an exported report shows where the seconds went.
    /// </summary>
    public sealed class StartupPhaseTrace : IDisposable
    {
        private readonly string _phase;
        private readonly Stopwatch _wall;
        private readonly string _entryLevel;
        private readonly IAppMemoryInfo _memory;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly IAppVisibility _visibility;
        private readonly object _activeGate = new object();
        private long _activeMs;
        private long _activeSegmentStartMs;
        private bool _activeRunning;
        private bool _closed;

        public StartupPhaseTrace(string phase, IAppMemoryInfo memory, IRuntimeDiagnostics diagnostics)
            : this(phase, memory, diagnostics, visibility: null)
        {
        }

        public StartupPhaseTrace(
            string phase,
            IAppMemoryInfo memory,
            IRuntimeDiagnostics diagnostics,
            IAppVisibility visibility)
        {
            _phase = phase ?? string.Empty;
            _memory = memory;
            _diagnostics = diagnostics;
            _visibility = visibility;
            _entryLevel = memory?.UsageLevel ?? string.Empty;
            _wall = Stopwatch.StartNew();

            bool startActive = visibility == null || visibility.IsWindowVisible;
            if (startActive)
            {
                _activeRunning = true;
                _activeSegmentStartMs = 0;
            }

            if (_visibility != null)
            {
                _visibility.Changed += OnVisibilityChanged;
            }

            _diagnostics?.Write(
                "startup-phase",
                _phase + ":begin",
                "memory=" + _entryLevel +
                "; bytes=" + (memory?.UsageBytes ?? 0UL));
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            if (_visibility != null)
            {
                _visibility.Changed -= OnVisibilityChanged;
            }

            long activeMs;
            lock (_activeGate)
            {
                FlushActiveLocked();
                activeMs = _activeMs;
            }

            _wall.Stop();
            _diagnostics?.Write(
                "startup-phase",
                _phase + ":end",
                "elapsedMs=" + _wall.ElapsedMilliseconds +
                "; activeMs=" + activeMs +
                "; memory=" + (_memory?.UsageLevel ?? string.Empty) +
                "; entryMemory=" + _entryLevel +
                "; bytes=" + (_memory?.UsageBytes ?? 0UL));
        }

        private void OnVisibilityChanged(object sender, EventArgs e)
        {
            if (_closed || _visibility == null)
            {
                return;
            }

            lock (_activeGate)
            {
                if (_visibility.IsWindowVisible)
                {
                    if (!_activeRunning)
                    {
                        _activeRunning = true;
                        _activeSegmentStartMs = _wall.ElapsedMilliseconds;
                    }
                }
                else
                {
                    FlushActiveLocked();
                }
            }
        }

        private void FlushActiveLocked()
        {
            if (!_activeRunning)
            {
                return;
            }

            long now = _wall.ElapsedMilliseconds;
            long delta = now - _activeSegmentStartMs;
            if (delta > 0)
            {
                _activeMs += delta;
            }

            _activeRunning = false;
            _activeSegmentStartMs = now;
        }
    }
}
