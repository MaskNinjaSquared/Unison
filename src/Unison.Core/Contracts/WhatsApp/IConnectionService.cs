using System;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Connection lifecycle facade: classifies disconnects, applies auto-unlink policy,
    /// and drives ClearSession / toast. WhatsAppService only owns the socket transport.
    /// </summary>
    public interface IConnectionService
    {
        /// <summary>
        /// Raised after policy evaluation (relink executed, skipped, or network-only).
        /// Shell uses <see cref="ConnectionEndedEventArgs.RequiresRelink"/> for UI cues;
        /// session wipe is performed by this facade when auto-unlink is enabled.
        /// </summary>
        event EventHandler<ConnectionEndedEventArgs> ConnectionEnded;

        /// <summary>
        /// True when the device reports an active internet path (not local-offline).
        /// </summary>
        bool HasInternetAccess { get; }

        /// <summary>
        /// User setting: automatically clear session and return to QR on logout/revocation.
        /// </summary>
        bool AutoUnlinkOnLogoutEnabled { get; }

        /// <summary>
        /// Wire the WhatsApp client after DI build (avoids ctor cycle with WhatsAppService).
        /// </summary>
        void AttachWhatsAppService(IWhatsAppService whatsApp);

        /// <summary>
        /// Socket layer observed a <c>stream:error</c> (or equivalent). Policy runs here.
        /// </summary>
        void NotifyStreamError(string code);

        /// <summary>
        /// Socket layer observed repeated open→close before login success (suspected revoke).
        /// </summary>
        void NotifySuspectedInvalidSession(string trigger);

        /// <summary>Map Baileys/stream codes to <see cref="DisconnectReason"/>.</summary>
        DisconnectReason ClassifyStreamError(string code);
    }
}
