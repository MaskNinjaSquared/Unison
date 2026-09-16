using System;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Lightweight runtime health journal for debug UI and crash diagnosis.
    /// </summary>
    public interface IRuntimeDiagnostics
    {
        /// <summary>
        /// Whether the chatty per-operation tracing is on. Persisted, so it survives a restart -
        /// a bug that only shows up during startup is otherwise impossible to capture.
        /// </summary>
        bool IsVerboseLoggingEnabled { get; }

        /// <summary>
        /// Turns verbose tracing on or off. <paramref name="source"/> only names the caller in the
        /// transition line, so a toggle that flips on its own can be traced back.
        /// </summary>
        void SetVerboseLogging(bool enabled, string source);

        void Start();
        void StartHealthSampling();
        void Write(string category, string eventName, string details = null);
        void RecordException(string category, string eventName, Exception exception, string details = null);
        string GetRecentText();
        RuntimeDiagnosticsSnapshot CaptureSnapshot();
        Task FlushAsync(string reason);
        Task<string> ExportReportAsync();

        /// <summary>
        /// Writes the full diagnostics report under LocalState/Diagnostics (reliable on Mobile
        /// when the save picker is unavailable). Returns the file path or an error string.
        /// </summary>
        Task<string> SaveReportToLocalFolderAsync();

        Task ClearAsync();
    }
}
