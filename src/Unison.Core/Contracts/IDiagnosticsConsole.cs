using System;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Everything the debug surface consumes, behind one contract.
    /// </summary>
    /// <remarks>
    /// This covers only the read side of diagnostics - inspecting, exporting, clearing and
    /// running experiments. The write side stays on <see cref="ISessionLogger"/> and
    /// <see cref="IRuntimeDiagnostics"/>, which production code paths inject directly: a view
    /// model that just records an event must not gain the ability to open a socket.
    /// </remarks>
    public interface IDiagnosticsConsole
    {
        /// <summary>Whether protocol traffic is being captured into the session log.</summary>
        bool IsCaptureEnabled { get; set; }

        /// <summary>
        /// Whether the chatty per-operation tracing is on. Separate from
        /// <see cref="IsCaptureEnabled"/>: that one decides whether lines are kept, this one
        /// decides whether they are produced at all.
        /// </summary>
        bool IsVerboseLoggingEnabled { get; }

        /// <summary>Turns verbose tracing on or off. <paramref name="source"/> names the caller.</summary>
        void SetVerboseLogging(bool enabled, string source);

        /// <summary>Raised for every line appended to the session log.</summary>
        event EventHandler<string> LogLineAppended;

        string GetCapturedLog();

        void ClearCapturedLog();

        Task SaveCapturedLogAsync();

        RuntimeDiagnosticsSnapshot CaptureRuntimeSnapshot();

        string GetRecentRuntimeText();

        Task<string> ExportRuntimeReportAsync();

        /// <summary>Saves the report under LocalState/Diagnostics (Mobile-friendly).</summary>
        Task<string> SaveRuntimeReportToLocalFolderAsync();

        Task ClearRuntimeLogAsync();

        /// <summary>False on builds where the Unison.Socket probe was not registered.</summary>
        bool IsSocketSliceAvailable { get; }

        bool IsSocketSliceRunning { get; }

        /// <summary>Progress lines from the socket slice, already formatted for display.</summary>
        event EventHandler<string> SocketSliceReported;

        /// <summary>Pairing QR payload produced by the slice, for the host to render.</summary>
        event EventHandler<string> SocketSliceQrReceived;

        Task RunSocketSliceAsync();

        Task StopSocketSliceAsync();
    }
}
