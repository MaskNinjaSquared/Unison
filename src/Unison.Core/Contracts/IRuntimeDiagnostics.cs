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
        void Start();
        void StartHealthSampling();
        void Write(string category, string eventName, string details = null);
        void RecordException(string category, string eventName, Exception exception, string details = null);
        string GetRecentText();
        RuntimeDiagnosticsSnapshot CaptureSnapshot();
        Task FlushAsync(string reason);
        Task<string> ExportReportAsync();
        Task ClearAsync();
    }
}
