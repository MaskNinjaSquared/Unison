using Unison.WhatsAppClient.Abstractions;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
        // Session-flag host surface (ConnectionSessionFlags). No reconnect policy here.

        bool IConnectionResumeHost.IsFatalSessionEnded => _sessionFlags.FatalSessionEnded;

        void IConnectionResumeHost.ClearSuppressReconnect() => _sessionFlags.SuppressReconnect = false;
    }
}
