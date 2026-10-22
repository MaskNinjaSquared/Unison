using System;
using System.Diagnostics;
using Unison.Core.Contracts;

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
        private readonly string _entryLevel;
        private readonly IAppMemoryInfo _memory;
        private bool _closed;

        internal StartupPhaseTrace(string phase, IAppMemoryInfo memory)
        {
            _phase = phase ?? string.Empty;
            _memory = memory;
            _entryLevel = memory?.UsageLevel ?? string.Empty;
            _elapsed = Stopwatch.StartNew();

            RuntimeDiagnosticsService.Instance.Write(
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
            _elapsed.Stop();
            RuntimeDiagnosticsService.Instance.Write(
                "startup-phase",
                _phase + ":end",
                "elapsedMs=" + _elapsed.ElapsedMilliseconds +
                "; memory=" + (_memory?.UsageLevel ?? string.Empty) +
                "; entryMemory=" + _entryLevel +
                "; bytes=" + (_memory?.UsageBytes ?? 0UL));
        }
    }
}
