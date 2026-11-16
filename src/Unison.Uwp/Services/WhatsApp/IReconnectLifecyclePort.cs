using System;
using System.Threading.Tasks;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// What <see cref="ReconnectHostAdapter"/> still needs from the connection owner:
    /// the connect lifecycle, status publishing, and the pairing stage-2 entry.
    /// </summary>
    internal interface IReconnectLifecyclePort
    {
        bool IsConnected { get; }

        Task InitializeAsync();

        Task ConnectAsync();

        void PublishReconnectingStatus();

        void RaiseReconnectLoopError(Exception error);

        void PairingTrace(string message);

        void MarkPairingRestartPending(string reason);

        void StopConnectionHealthMonitor(string reason);

        void FireAndForgetPairingStage2Reconnect();
    }
}
