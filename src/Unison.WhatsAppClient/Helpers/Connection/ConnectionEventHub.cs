// =============================================================================
// ConnectionEventHub
//
// Connection-level events the ConnectionFacade listens to. One instance per app
// (DI singleton); wire owners raise here instead of through the host, so the
// host is no longer the event source. Raises invoke subscribers directly: a
// throwing subscriber propagates exactly as it did from the host.
// =============================================================================
using System;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ConnectionEventHub : IConnectionEventSource
    {
        public string CurrentConnectionStatus { get; set; }

        public event EventHandler<string> OnConnectionUpdate;

        public event EventHandler OnSessionInitialized;

        public event EventHandler<SessionClearedEventArgs> OnSessionCleared;

        public event EventHandler<Exception> OnError;

        public event EventHandler<string> OnQRCodeReceived;

        public event EventHandler OnQrExpired;

        public event EventHandler<string> OnStreamError;

        public event EventHandler<string> OnInvalidSessionSuspected;

        public void RaiseConnectionUpdate(string status) =>
            OnConnectionUpdate?.Invoke(this, status);

        public void RaiseSessionInitialized() =>
            OnSessionInitialized?.Invoke(this, EventArgs.Empty);

        public void RaiseSessionCleared(SessionClearedEventArgs args) =>
            OnSessionCleared?.Invoke(this, args);

        public void RaiseError(Exception error) =>
            OnError?.Invoke(this, error);

        public void RaiseQrCodeReceived(string qr) =>
            OnQRCodeReceived?.Invoke(this, qr);

        public void RaiseQrExpired() =>
            OnQrExpired?.Invoke(this, EventArgs.Empty);

        public void RaiseStreamError(string code) =>
            OnStreamError?.Invoke(this, code);

        public void RaiseInvalidSessionSuspected(string trigger) =>
            OnInvalidSessionSuspected?.Invoke(this, trigger);
    }
}
