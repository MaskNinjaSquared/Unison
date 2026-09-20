// =============================================================================
// IWhatsAppSocket
//
// Everything WhatsAppService needs from a connection, and nothing else.
//
// It is deliberately not a designed interface: it is the exact surface the
// legacy SocketClient already exposes to its one consumer, written down. That is
// what makes it useful - with the dependency stated, a second implementation can
// be built on Unison.Socket and swapped in without WhatsAppService noticing that
// the connection underneath it changed.
//
// The shape here is the legacy shape, warts included: connection state as a
// string, receipts as raw nodes, media as a byte array. Straightening that out is
// the job of the facades, which talk to the new stack directly. This interface
// only has to be faithful enough to let the old class keep working while the new
// one takes over the wire.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Core.Models;

namespace Unison.Uwp.Client
{
    public interface IWhatsAppSocket : IDisposable
    {
        // -- Events ----------------------------------------------------------

        /// <summary>Credentials moved and should be persisted.</summary>
        event EventHandler OnAuthStateUpdate;

        /// <summary>Login completed and the session is usable.</summary>
        event EventHandler OnSessionInitialized;

        event EventHandler<BinaryNode> OnLinkCodeCompanionReg;

        /// <summary>A raw message stanza, before decryption.</summary>
        event EventHandler<BinaryNode> OnMessage;

        event EventHandler<BinaryNode> OnReceiptReceived;

        event EventHandler<Exception> OnError;

        /// <summary>A message we know exists but could not read.</summary>
        event EventHandler<MissingMessageEventArgs> OnMissingMessageDetected;

        event EventHandler<OutgoingMessageStatusEventArgs> OnOutgoingMessageStatusChanged;

        event EventHandler<PresenceUpdateEventArgs> OnPresenceUpdate;

        event EventHandler<global::Proto.HistorySync> OnHistorySyncReceived;

        /// <summary>Connection state as a string, in the legacy vocabulary.</summary>
        event EventHandler<string> OnConnectionUpdate;

        event EventHandler<string> OnQRCodeReceived;

        event EventHandler<string> OnStreamError;

        event Func<object, DecryptedMessageEventArgs, Task> OnDecryptedMessageReceived;

        /// <summary>The server says a collection changed and should be re-synced.</summary>
        event Func<object, DirtyNotificationEventArgs, Task> OnDirtyNotificationReceived;

        /// <summary>The offline backlog has been delivered, with how many stanzas it held.</summary>
        event Func<object, int, Task> OnReceivedPendingNotifications;

        event Func<object, string, Task> OnServerSyncCollectionReceived;

        // -- State -----------------------------------------------------------

        AuthState Auth { get; }

        IKeyStore KeyStore { get; }

        FileKeyStore PersistentKeyStore { get; }

        bool IsConnected { get; }

        bool IsHandshakeComplete { get; }

        /// <summary>True while the first sync after login is still arriving.</summary>
        bool IsAwaitingInitialSync { get; }

        /// <summary>True when the background broker holds the socket instead of us.</summary>
        bool IsSocketOwnedByBroker { get; }

        bool HasFreshConnection(TimeSpan maximumSilence);

        bool HasStalledNodeProcessing(TimeSpan maximumStall);

        // -- Diagnostics -----------------------------------------------------

        int QueuedNodeProcessingCount { get; }

        int PendingQueryCount { get; }

        long DecodedNodeCount { get; }

        long InboundFrameCount { get; }

        DateTime LastInboundFrameUtc { get; }

        DateTime LastNodeProcessingProgressUtc { get; }

        string TransportName { get; }

        // -- Lifecycle -------------------------------------------------------

        Task ConnectAsync();

        void Disconnect();

        /// <summary>
        /// Tells WhatsApp to unlink this device, then closes. <see cref="Disconnect"/> only drops
        /// the connection; this is what makes the phone stop listing us as a linked device.
        /// </summary>
        Task LogoutAsync(string reason = null);

        Task InitializeKeyStoreAsync();

        /// <summary>Round-trips a ping to tell a live socket from one that only looks alive.</summary>
        Task<bool> ProbeConnectionAsync(int timeoutMs = 12000);

        /// <summary>Hands the socket to the background broker so it survives suspension.</summary>
        Task<bool> TransferSocketToBrokerAsync(string reason);

        /// <summary>Takes the socket back from the broker when the app returns to the foreground.</summary>
        Task<bool> ReclaimSocketFromBrokerAsync();

        // -- Operations ------------------------------------------------------

        string GenerateMessageId();

        Task<BinaryNode> QueryGroupMetadataAsync(string groupJid);

        Task<BinaryNode> QueryParticipatingGroupsAsync();

        Task<BinaryNode> QueryUsyncAsync(
            List<BinaryNode> userNodes,
            string context,
            string mode,
            List<BinaryNode> queryProtocols,
            int timeoutMs = 60000);

        Task<ProfilePictureResult> GetProfilePictureUrlResultAsync(string jid, string type = "preview");

        Task<byte[]> DownloadAndDecryptMediaAsync(
            string url,
            string directPath,
            byte[] mediaKey,
            string mediaType,
            byte[] expectedFileEncSha256 = null);

        Task PresenceSubscribeAsync(string toJid);

        /// <summary>Pins or unpins a conversation for the whole account.</summary>
        Task SetChatPinnedAsync(string jid, bool pinned);

        /// <summary>
        /// Deletes a conversation for the whole account. The range names the tail it covers.
        /// </summary>
        Task DeleteChatAsync(string jid, IEnumerable<Unison.Socket.AppState.RangeMessage> lastMessages);

        /// <summary>Clears the conversation's unread state across the account's devices.</summary>
        Task MarkChatReadAsync(string jid, IEnumerable<Unison.Socket.AppState.RangeMessage> lastMessages);

        /// <summary>Tells the senders of those messages that they were read.</summary>
        Task MarkMessagesReadAsync(IEnumerable<Unison.Socket.UseCases.Messages.ReceiptTarget> targets);

        Task<string> SendTextMessageAsync(string jid, string text, string explicitMessageId = null);

        Task<string> SendImageMessageAsync(string jid, byte[] imageBytes, string caption = null);

        Task<string> SendAudioMessageAsync(
            string jid,
            byte[] audioBytes,
            string mimeType,
            uint durationSeconds,
            bool isVoiceMessage = false);

        Task<string> SendPinInChatMessageAsync(
            string jid,
            global::Proto.MessageKey targetKey,
            bool pin,
            uint durationSeconds = 604800);

        Task<string> RequestHistorySyncOnDemandAsync(
            string jid,
            string lastMsgId,
            bool lastMsgFromMe,
            long lastMsgTimestamp,
            int count,
            string explicitStanzaId = null);

        /// <param name="requestId">
        /// Identifies the request inside the chunks that answer it, which is what lets a caller
        /// tell its own history from whatever else the phone is sending. Different from the
        /// stanza id: that one only names the ack.
        /// </param>
        Task<string> RequestFullHistorySyncOnDemandAsync(string explicitStanzaId = null, string requestId = null);

        Task<string> RequestPlaceholderResendAsync(
            global::Proto.MessageKey messageKey,
            string explicitStanzaId = null);

        Task StoreTcTokenAsync(string jid, byte[] token, long? timestamp, long? senderTimestamp, string source);

        void RegisterJidAlias(string jidA, string jidB, string source, bool writeLog = true);

        void RegisterJidAliases(IDictionary<string, string> aliases, string source);
    }
}
