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
        /// Raised after policy evaluation (relink executed or network-only).
        /// Shell uses <see cref="ConnectionEndedEventArgs.RequiresRelink"/> for UI cues;
        /// explicit phone revoke (401 / device_removed) always wipes via this facade.
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

        /// <summary>
        /// Leaves the account on purpose: tells WhatsApp to unlink this device, then wipes
        /// everything local and returns the app to pairing.
        /// </summary>
        /// <remarks>
        /// The order is the whole point. Wiping first would leave us with no identity to unlink
        /// with, and the device would stay listed on the phone until the user removed it there.
        /// </remarks>
        System.Threading.Tasks.Task LogoutAsync(string reason = null);

        // ---------------------------------------------------------------------
        // Pairing: linking a device that has no credentials yet
        //
        // The other end of the same lifecycle this facade already owns. It is here rather than
        // on the client so the pairing screen has one thing to talk to, and so the client stays
        // free to be replaced underneath it.
        // ---------------------------------------------------------------------

        /// <summary>A code to display. Arrives again every time the server rotates it.</summary>
        event EventHandler<string> QrReceived;

        /// <summary>
        /// The code on screen is dead and nothing will replace it on its own, so the pairing
        /// screen has to offer a reload.
        /// </summary>
        event EventHandler QrExpired;

        /// <summary>Transport progress, in the client's vocabulary: connecting, open, close.</summary>
        event EventHandler<string> StatusChanged;

        /// <summary>
        /// Login succeeded and the session is usable. The point where the app can leave the
        /// pairing surface and show conversations.
        /// </summary>
        event EventHandler SessionEstablished;

        /// <summary>
        /// A local session wipe. Fires twice by design: once so the UI can leave immediately, and
        /// again once the credentials are gone and pairing can start
        /// (<see cref="Models.SessionClearedEventArgs.StartPairing"/>).
        /// </summary>
        event EventHandler<Models.SessionClearedEventArgs> SessionCleared;

        /// <summary>Something went wrong far enough from the UI that only this reports it.</summary>
        event EventHandler<Exception> Failed;

        /// <summary>The last status seen, for a screen that hooked up after it was raised.</summary>
        string CurrentStatus { get; }

        /// <summary>
        /// Opens a connection so the server sends a pair-device request. Also the reload: an
        /// unregistered session gets a fresh transport, because the request only ever arrives on
        /// a new one.
        /// </summary>
        System.Threading.Tasks.Task StartPairingAsync();

        /// <summary>
        /// Asks for the eight-character code the user types on their phone instead of scanning.
        /// Connects first when needed; null means pairing this way is not available.
        /// </summary>
        System.Threading.Tasks.Task<string> RequestPairingCodeAsync(string phoneNumber);

        /// <summary>
        /// Throws away everything local and returns to pairing, without telling WhatsApp. Unlike
        /// <see cref="LogoutAsync"/> the device stays linked on the phone, which is why this is a
        /// developer escape hatch and not something to offer a user.
        /// </summary>
        System.Threading.Tasks.Task ClearLocalSessionAsync();
    }
}
