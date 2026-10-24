// =============================================================================
// IAutoReconnectLoopHost
//
// Side effects for AutoReconnectLoopDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IAutoReconnectLoopHost
    {
        bool SuppressReconnect { get; }

        bool FatalSessionEnded { get; }

        bool PairingRestartPending { get; }

        bool IsAuthStatePresent { get; }

        bool IsAuthRegistered { get; }

        Task<bool> IsCurrentSocketHealthyAsync();

        Task InitializeAsync();

        Task ConnectAsync();

        TimeSpan ReconnectDelayForAttempt(int attempt);

        void PublishReconnectingStatus();

        void LogAutoReconnect(string message);

        void TracePairing(string detail);

        void RaiseReconnectLoopError(Exception error);
    }
}
