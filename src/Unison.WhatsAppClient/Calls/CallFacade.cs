// =============================================================================
// CallFacade
//
// UI → CallSession → signaling channel. No WinRT; media gated by IsMediaAvailable.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Voip.Abstractions;
using Unison.Voip.Session;

namespace Unison.WhatsAppClient.Calls
{
    public sealed class CallFacade : ICallService
    {
        private readonly ICallSignalingChannel _signaling;

        public CallFacade(ICallSignalingChannel signaling)
        {
            _signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
        }

        /// <summary>Engine media is not ported yet — keep UI “unavailable” for place/answer.</summary>
        public bool IsMediaAvailable => false;

        public Task RejectAsync(
            string callId,
            string peerJid,
            string callCreatorJid,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var session = new CallSession(
                callId,
                peerJid,
                callCreatorJid,
                isVideo: false,
                inbound: true,
                _signaling);
            return session.RejectAsync(cancellationToken);
        }

        public Task HangupAsync(
            string callId,
            string peerJid,
            string callCreatorJid,
            string reason = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var session = new CallSession(
                callId,
                peerJid,
                callCreatorJid,
                isVideo: false,
                inbound: false,
                _signaling);
            // Hangup is valid from Connecting/Active; outbound starts Connecting.
            return session.HangupAsync(reason, cancellationToken);
        }

        /// <summary>Builds a session for hosts that need Answer / state without sending yet.</summary>
        public CallSession CreateInboundSession(
            string callId,
            string peerJid,
            string callCreatorJid,
            bool isVideo)
        {
            return new CallSession(
                callId,
                peerJid,
                callCreatorJid,
                isVideo,
                inbound: true,
                _signaling);
        }
    }
}
