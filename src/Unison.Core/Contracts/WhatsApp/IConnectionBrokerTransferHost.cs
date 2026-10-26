using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What <c>ConnectionBrokerTransfer</c> asks of the connection client when moving
    /// a live socket to the out-of-process broker.
    /// </summary>
    public interface IConnectionBrokerTransferHost
    {
        /// <summary>True when a connected, handshake-complete socket exists.</summary>
        bool HasReadyForegroundSocket { get; }

        /// <summary>True when the live socket is already owned by the broker.</summary>
        bool IsAlreadyBrokerOwned { get; }

        void StopHealthMonitor(string reason);

        void RestartHealthMonitor();

        Task PersistBackgroundDisplayNamesAsync();

        Task PrepareForSuspendAsync();

        /// <summary>
        /// Moves ownership to the broker. Must answer true only when ownership moved.
        /// </summary>
        Task<bool> TransferSocketToBrokerAsync(string reason);

        void PublishBackgroundConnection();

        void WriteBrokerJournal(string eventName, string detail);

        void RecordBrokerException(string eventName, Exception error);
    }
}
