using Unison.WhatsAppClient.Abstractions;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
        // Session-flag host surface (ConnectionSessionFlags). No reconnect policy here.

        bool ITryStartPairingStage2ReconnectHost.SuppressReconnect => _sessionFlags.SuppressReconnect;

        bool ITryStartPairingStage2ReconnectHost.FatalSessionEnded => _sessionFlags.FatalSessionEnded;

        bool ISoftReconnectIoHost.SuppressReconnect => _sessionFlags.SuppressReconnect;

        bool ISoftReconnectIoHost.FatalSessionEnded => _sessionFlags.FatalSessionEnded;

        bool ISoftReconnectIoHost.PairingRestartPending => _sessionFlags.PairingRestartPending;

        bool IConnectionResumeHost.IsFatalSessionEnded => _sessionFlags.FatalSessionEnded;

        void IConnectionResumeHost.ClearSuppressReconnect() => _sessionFlags.SuppressReconnect = false;

        bool IIsCurrentSocketHealthyHost.SuppressReconnect
        {
            get => _sessionFlags.SuppressReconnect;
            set => _sessionFlags.SuppressReconnect = value;
        }

        bool IAutoReconnectLoopHost.SuppressReconnect => _sessionFlags.SuppressReconnect;

        bool IAutoReconnectLoopHost.FatalSessionEnded => _sessionFlags.FatalSessionEnded;

        bool IAutoReconnectLoopHost.PairingRestartPending => _sessionFlags.PairingRestartPending;
    }
}
