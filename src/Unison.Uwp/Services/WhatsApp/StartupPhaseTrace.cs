using System;
using System.Diagnostics;
using Windows.System;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Brackets a startup phase in the runtime journal with its duration and the memory level
    /// at both ends, so an exported report shows where the seconds went.
    /// </summary>
    internal sealed class StartupPhaseTrace : IDisposable
    {
        private readonly string _phase;
        private readonly Stopwatch _elapsed;
        private readonly AppMemoryUsageLevel _entryLevel;
        private bool _closed;

        internal StartupPhaseTrace(string phase)
        {
            _phase = phase ?? string.Empty;
            _entryLevel = MemoryManager.AppMemoryUsageLevel;
            _elapsed = Stopwatch.StartNew();

            RuntimeDiagnosticsService.Instance.Write(
                "startup-phase",
                _phase + ":begin",
                "memory=" + _entryLevel +
                "; bytes=" + MemoryManager.AppMemoryUsage);
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _elapsed.Stop();
            RuntimeDiagnosticsService.Instance.Write(
                "startup-phase",
                _phase + ":end",
                "elapsedMs=" + _elapsed.ElapsedMilliseconds +
                "; memory=" + MemoryManager.AppMemoryUsageLevel +
                "; entryMemory=" + _entryLevel +
                "; bytes=" + MemoryManager.AppMemoryUsage);
        }
    }
}
