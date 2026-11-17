using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>
    /// What <see cref="Helpers.ReconnectHost"/> still needs from the connection owner:
    /// the connect lifecycle, status publishing, pairing traces and the pairing stage-2 entry.
    /// </summary>
    public interface IReconnectLifecyclePort
    {
        bool IsConnected { get; }

        Task InitializeAsync();

        Task ConnectAsync();

        void PublishReconnectingStatus();

        void PairingTrace(string message);

        /// <summary>Always-on pairing journal line, written only while a pairing trace is active.</summary>
        void TraceScheduleAutoReconnectReason(string reason);

        void MarkPairingRestartPending(string reason);

        void StopConnectionHealthMonitor(string reason);

        void FireAndForgetPairingStage2Reconnect();
    }
}
