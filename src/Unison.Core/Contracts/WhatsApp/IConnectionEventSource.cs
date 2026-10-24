// =============================================================================
// IConnectionEventSource
//
// Connection facts the ConnectionFacade relays to UI (QR, status, session,
// stream errors). Same event names as IWhatsAppService so one field satisfies
// both; the façade must not take the full client contract.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IConnectionEventSource
    {
        string CurrentConnectionStatus { get; }

        event EventHandler<string> OnConnectionUpdate;

        event EventHandler OnSessionInitialized;

        event EventHandler<SessionClearedEventArgs> OnSessionCleared;

        event EventHandler<Exception> OnError;

        event EventHandler<string> OnQRCodeReceived;

        event EventHandler OnQrExpired;

        event EventHandler<string> OnStreamError;

        event EventHandler<string> OnInvalidSessionSuspected;
    }
}
