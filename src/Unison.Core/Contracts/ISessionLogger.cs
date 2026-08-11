using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    public interface ISessionLogger
    {
        bool Enabled { get; set; }

        /// <summary>
        /// Temporary pairing/QR trace: Diag lines are captured even when Enabled is false.
        /// Does not persist to LocalSettings.
        /// </summary>
        bool PairingTraceActive { get; set; }

        string GetLogText();
        void Clear();
        Task SaveToFileAsync();
        event EventHandler<string> OnLogUpdated;

        /// <summary>Always appends to the on-device log buffer (ignores Enabled).</summary>
        void WriteAlways(string message);

        /// <summary>Always appends an error (with optional exception detail) to the log buffer.</summary>
        void WriteErrorAlways(string message, Exception ex = null);
    }
}
