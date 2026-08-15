using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Args for <see cref="Contracts.WhatsApp.IConnectionService.ConnectionEnded"/>.
    /// </summary>
    public sealed class ConnectionEndedEventArgs : EventArgs
    {
        public ConnectionEndedEventArgs(DisconnectReason reason, string code, string message)
        {
            Reason = reason;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public DisconnectReason Reason { get; }
        public string Code { get; }
        public string Message { get; }

        /// <summary>
        /// True when the credentials are gone for good and the only way back is a new QR scan.
        /// </summary>
        /// <remarks>
        /// Only an explicit logout counts, because this is what authorizes deleting the local
        /// session - and the session on the phone is the one thing we cannot check. A replaced
        /// connection and a bad session both used to land here, and both happen while the device
        /// is still perfectly linked: opening WhatsApp Web elsewhere, or a stream error the
        /// server sent without a code, which the socket reports as 500 by convention. Those
        /// stop us reconnecting, which <see cref="ShouldStopReconnecting"/> covers; they do not
        /// entitle us to throw the account away.
        /// </remarks>
        public bool RequiresRelink
        {
            get { return Reason == DisconnectReason.LoggedOut; }
        }

        /// <summary>
        /// True when retrying would only repeat the refusal - the session was taken over or
        /// denied. The credentials stay where they are.
        /// </summary>
        /// <remarks>
        /// A bad session is deliberately absent: rc14 reconnects on 500 like it does on any
        /// transient code, and 500 is also what the socket reports when the server ends a stream
        /// without saying why. Backing off permanently on that would leave a healthy link sitting
        /// disconnected until the app is restarted.
        /// </remarks>
        public bool ShouldStopReconnecting
        {
            get
            {
                return Reason == DisconnectReason.LoggedOut ||
                       Reason == DisconnectReason.ConnectionReplaced ||
                       Reason == DisconnectReason.Forbidden;
            }
        }
    }
}
