using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Diagnostics
{
    /// <summary>
    /// Composes the session log, the runtime journal and the socket probe into the single
    /// surface the debug pane talks to.
    /// </summary>
    /// <remarks>
    /// The probe is optional so a build can ship without the experimental socket stack, in which
    /// case <see cref="IsSocketSliceAvailable"/> is false and the section hides itself.
    /// </remarks>
    public sealed class DiagnosticsConsole : IDiagnosticsConsole
    {
        private readonly ISessionLogger _sessionLogger;
        private readonly IRuntimeDiagnostics _runtimeDiagnostics;
        private readonly ISocketSliceProbe _socketSliceProbe;

        public DiagnosticsConsole(
            ISessionLogger sessionLogger,
            IRuntimeDiagnostics runtimeDiagnostics,
            ISocketSliceProbe socketSliceProbe = null)
        {
            if (sessionLogger == null)
            {
                throw new ArgumentNullException(nameof(sessionLogger));
            }

            if (runtimeDiagnostics == null)
            {
                throw new ArgumentNullException(nameof(runtimeDiagnostics));
            }

            _sessionLogger = sessionLogger;
            _runtimeDiagnostics = runtimeDiagnostics;
            _socketSliceProbe = socketSliceProbe;

            _sessionLogger.OnLogUpdated += (s, line) => LogLineAppended?.Invoke(this, line);

            if (_socketSliceProbe != null)
            {
                _socketSliceProbe.Reported += (s, line) => SocketSliceReported?.Invoke(this, line);
                _socketSliceProbe.QrReceived += (s, qr) => SocketSliceQrReceived?.Invoke(this, qr);
            }
        }

        public event EventHandler<string> LogLineAppended;

        public event EventHandler<string> SocketSliceReported;

        public event EventHandler<string> SocketSliceQrReceived;

        public bool IsCaptureEnabled
        {
            get => _sessionLogger.Enabled;
            set => _sessionLogger.Enabled = value;
        }

        public bool IsSocketSliceAvailable => _socketSliceProbe != null;

        public bool IsSocketSliceRunning => _socketSliceProbe != null && _socketSliceProbe.IsRunning;

        public string GetCapturedLog() => _sessionLogger.GetLogText();

        public void ClearCapturedLog() => _sessionLogger.Clear();

        public Task SaveCapturedLogAsync() => _sessionLogger.SaveToFileAsync();

        public RuntimeDiagnosticsSnapshot CaptureRuntimeSnapshot() => _runtimeDiagnostics.CaptureSnapshot();

        public string GetRecentRuntimeText() => _runtimeDiagnostics.GetRecentText();

        public Task<string> ExportRuntimeReportAsync() => _runtimeDiagnostics.ExportReportAsync();

        public Task ClearRuntimeLogAsync() => _runtimeDiagnostics.ClearAsync();

        public Task RunSocketSliceAsync()
        {
            return _socketSliceProbe == null || _socketSliceProbe.IsRunning
                ? Task.FromResult(true)
                : _socketSliceProbe.RunAsync();
        }

        public Task StopSocketSliceAsync()
        {
            return _socketSliceProbe == null ? Task.FromResult(true) : _socketSliceProbe.StopAsync();
        }
    }
}
