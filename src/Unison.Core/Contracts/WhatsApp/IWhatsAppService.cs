using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IWhatsAppService
    {
        string CurrentConnectionStatus { get; }
        bool IsConnected { get; }

        // ---------------------------------------------------------------------
        // Raw client events.
        //
        // For the facades only. Each one is re-published by the facade that owns the subject -
        // connection, history, contacts, messages, profile - and screens listen there. Anything
        // subscribing here directly is coupling itself to the client that happens to produce the
        // event today, which is exactly what the facades exist to prevent.
        // ---------------------------------------------------------------------

        event EventHandler<string> OnConnectionUpdate;
        event EventHandler<HistorySync> OnHistorySyncReceived;
        event EventHandler OnDisplayNamesUpdated;
        event EventHandler OnSessionInitialized;
        /// <summary>
        /// Raised during/after a local session wipe. May fire twice: once to show Login
        /// immediately (<see cref="SessionClearedEventArgs.StartPairing"/> = false), then
        /// again when auth is gone and pairing/QR can start.
        /// </summary>
        event EventHandler<SessionClearedEventArgs> OnSessionCleared;
        event EventHandler<Exception> OnError;
        event EventHandler<string> OnChatMessagesChanged;
        event EventHandler<string> OnQRCodeReceived;
        /// <summary>
        /// The code on screen is dead: the server ran out of refs to rotate through, or the
        /// socket dropped before anyone scanned it. Nothing reconnects on its own here, so the
        /// pairing surface has to offer a reload.
        /// </summary>
        event EventHandler OnQrExpired;
        /// <summary>Presence / chatstate for the subscribed contact (forwards SocketClient).</summary>
        event EventHandler<PresenceUpdateEventArgs> OnPresenceUpdate;
        /// <summary>
        /// A stream error arrived with this code. Reporting only: the client does not classify it
        /// and does not decide whether the session should be dropped — see
        /// <see cref="IConnectionService.ClassifyStreamError"/>.
        /// </summary>
        event EventHandler<string> OnStreamError;
        /// <summary>
        /// The shape of the failures looks like a bad session (repeated closes before login). A
        /// guess, not a verdict, and deliberately not acted on here.
        /// </summary>
        event EventHandler<string> OnInvalidSessionSuspected;
        /// <summary>A live status@broadcast item was decoded. It is not a chat and never routes to one.</summary>
        event EventHandler<HistoryStatus> OnLiveStatusReceived;
        /// <summary>A background FULL_HISTORY solicitation went out; the toast gate counts these.</summary>
        event EventHandler OnBackgroundHistorySyncBounced;
        /// <summary>An avatar file was written to the local cache for a JID.</summary>
        event EventHandler<AvatarCachedEventArgs> OnAvatarCached;
        /// <summary>The background resolution pass reached its group step; the group façade runs the listing.</summary>
        event EventHandler OnGroupListingDue;
        /// <summary>A phone JID and a LID were newly paired.</summary>
        event EventHandler<JidAliasResolvedEventArgs> OnJidAliasResolved;
        /// <summary>
        /// After a burst of new pairs, the rows still without an avatar under them. Raised on the
        /// UI thread; the contact façade decides how they are asked again.
        /// </summary>
        event EventHandler<AvatarRetryDueEventArgs> OnAvatarRetryDue;

        Task InitializeAsync();
        Task ConnectAsync();
        Task ResumeAsync();
        Task<bool> IsRegisteredAsync();
        Task ClearSessionAsync();

        /// <summary>
        /// Asks WhatsApp to unlink this device, then drops the socket. Only the server side:
        /// local data is wiped by <see cref="ClearSessionAsync"/>, and callers that want both
        /// should go through <see cref="IConnectionService.LogoutAsync"/>.
        /// </summary>
        Task NotifyServerLogoutAsync(string reason = null);

        void Disconnect();

        /// <summary>
        /// Called by <see cref="IConnectionService"/> when auto-unlink policy decides the
        /// session is dead. Socket-only: stop reconnect storms. Does not wipe auth/UI.
        /// </summary>
        void SuppressReconnectFromPolicy(string reason);

        /// <summary>Loads auth/session without opening the socket (fast launch).</summary>
        Task InitializeConnectionStateAsync();

        /// <summary>Ensures a healthy socket within timeoutMs.</summary>
        Task EnsureConnectedAsync(int timeoutMs = 35000, bool forceFreshTransport = false);

        Task ReleaseMemoryAsync();
        Task<bool> TransferActiveSocketToBrokerAsync(string reason);
        Task PrepareForSuspendAsync();
        Task ShutdownAsync(bool persist = true);
    }
}
