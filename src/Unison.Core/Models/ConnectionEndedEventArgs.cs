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
        /// True when the linked session is dead and the user must re-pair (QR).
        /// </summary>
        public bool RequiresRelink
        {
            get
            {
                return Reason == DisconnectReason.LoggedOut ||
                       Reason == DisconnectReason.ConnectionReplaced ||
                       Reason == DisconnectReason.BadSession ||
                       Reason == DisconnectReason.Forbidden;
            }
        }
    }
}
