// =============================================================================
// StartupPhaseTrace
//
// Brackets a startup phase in the runtime journal with duration and memory
// level at both ends.
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
        private readonly Stopwatch _elapsed;
        private readonly string _entryLevel;
        private readonly IAppMemoryInfo _memory;
        private readonly IRuntimeDiagnostics _diagnostics;
        private bool _closed;

        public StartupPhaseTrace(string phase, IAppMemoryInfo memory, IRuntimeDiagnostics diagnostics)
        {
            _phase = phase ?? string.Empty;
            _memory = memory;
            _diagnostics = diagnostics;
            _entryLevel = memory?.UsageLevel ?? string.Empty;
            _elapsed = Stopwatch.StartNew();

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
            _elapsed.Stop();
            _diagnostics?.Write(
                "startup-phase",
                _phase + ":end",
                "elapsedMs=" + _elapsed.ElapsedMilliseconds +
                "; memory=" + (_memory?.UsageLevel ?? string.Empty) +
                "; entryMemory=" + _entryLevel +
                "; bytes=" + (_memory?.UsageBytes ?? 0UL));
        }
    }
}
