using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What <c>ConnectionPairingStage2</c> asks of the connection client after close 515.
    /// </summary>
    public interface IConnectionPairingStage2Host
    {
        /// <summary>True when suppress/fatal latches block the stage-2 reconnect.</summary>
        bool ShouldAbortPairingRestart { get; }

        /// <summary>True when credentials already exist (failed stage-2 may soft-reconnect).</summary>
        bool IsRegistered { get; }

        /// <summary>True when soft reconnect is still allowed after a stage-2 failure.</summary>
        bool CanScheduleSoftReconnect { get; }

        void ClearPairingRestartPending();

        void ClearReconnectingFlag();

        Task ConnectAsync();

        void RequestSoftReconnect(string reason);

        void RaisePairingError(Exception error);

        void TracePairing(string detail);

        void LogPairingReset();

        string CurrentConnectionStatusOrNull { get; }
    }
}
