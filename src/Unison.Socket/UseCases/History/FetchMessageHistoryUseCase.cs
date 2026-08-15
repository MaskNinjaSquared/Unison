// =============================================================================
// FetchMessageHistoryUseCase
//
// Asks the phone for messages older than the ones we have.
//
// The companion only ever receives the history the phone chose to push. When the
// user scrolls past the top of a chat, this is what fetches more: the request
// names the oldest message we hold, and the answer arrives later as an ordinary
// history chunk of type ON_DEMAND.
//
// Ports: rc14 fetchMessageHistory in src/Socket/messages-recv.ts
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Socket.Messages;
using Unison.Socket.Sync;
using Unison.Socket.UseCases.Peer;

namespace Unison.Socket.UseCases.History
{
    public sealed class FetchMessageHistoryUseCase
    {
        private readonly SendPeerDataOperationMessageUseCase _peer;

        public FetchMessageHistoryUseCase(SendPeerDataOperationMessageUseCase peer)
        {
            if (peer == null)
            {
                throw new ArgumentNullException(nameof(peer));
            }

            _peer = peer;
        }

        /// <param name="count">How many messages to ask for.</param>
        /// <param name="oldestKey">The oldest message we already have in that chat.</param>
        /// <param name="oldestTimestampMs">Its timestamp, in milliseconds.</param>
        /// <returns>
        /// The request id. The messages themselves arrive later as an ON_DEMAND history chunk
        /// carrying this id as its session, not as a reply to this call.
        /// </returns>
        /// <param name="messageId">The stanza id to send under, for a caller that tracks the ack.</param>
        public Task<string> ExecuteAsync(
            int count,
            MessageEnvelopeKey oldestKey,
            long oldestTimestampMs,
            string messageId = null)
        {
            if (oldestKey == null || string.IsNullOrEmpty(oldestKey.Id))
            {
                throw new ArgumentException("The oldest message key is required", nameof(oldestKey));
            }

            var request = new global::Proto.Message.Types.PeerDataOperationRequestMessage
            {
                PeerDataOperationRequestType =
                    global::Proto.Message.Types.PeerDataOperationRequestType.HistorySyncOnDemand,
                HistorySyncOnDemandRequest =
                    new global::Proto.Message.Types.PeerDataOperationRequestMessage.Types.HistorySyncOnDemandRequest
                    {
                        ChatJid = oldestKey.RemoteJid ?? string.Empty,
                        OldestMsgFromMe = oldestKey.FromMe,
                        OldestMsgId = oldestKey.Id,
                        OldestMsgTimestampMs = oldestTimestampMs,
                        OnDemandMsgCount = count
                    }
            };

            return _peer.ExecuteAsync(request, messageId);
        }

        /// <summary>
        /// Asks for the whole archive rather than one chat's older messages. The phone answers
        /// with a long series of chunks over minutes, so this is a user-initiated action and not
        /// something to run at login.
        /// </summary>
        /// <param name="messageId">
        /// The stanza id to send under. The phone's ack names it, which is how a caller knows the
        /// request was accepted rather than lost.
        /// </param>
        /// <param name="requestId">
        /// Identifies the request inside the chunks that answer it. This is a different thing
        /// from the stanza id and the two must not be shared.
        /// </param>
        public Task<string> ExecuteFullAsync(string messageId = null, string requestId = null)
        {
            var request = new global::Proto.Message.Types.PeerDataOperationRequestMessage
            {
                PeerDataOperationRequestType =
                    global::Proto.Message.Types.PeerDataOperationRequestType.FullHistorySyncOnDemand,
                FullHistorySyncOnDemandRequest =
                    new global::Proto.Message.Types.PeerDataOperationRequestMessage.Types.FullHistorySyncOnDemandRequest
                    {
                        RequestMetadata = new global::Proto.Message.Types.FullHistorySyncOnDemandRequestMetadata
                        {
                            RequestId = requestId ?? Guid.NewGuid().ToString("N")
                        },

                        // The phone tailors the chunks to what we say we can read, so this has to
                        // be the same declaration login made.
                        HistorySyncConfig = HistorySyncConfigFactory.Create()
                    }
            };

            return _peer.ExecuteAsync(request, messageId);
        }
    }
}
