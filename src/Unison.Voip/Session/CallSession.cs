// =============================================================================
// CallSession
//
// Lifecycle for one WhatsApp call (meowcaller Call). Signaling goes through
// ICallSignalingChannel; CallEngine owns media phase hooks until MLow lands.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;
using Unison.Voip.Engine;

namespace Unison.Voip.Session
{
    public sealed class CallSession
    {
        private readonly ICallSignalingChannel _signaling;
        private readonly object _gate = new object();

        public CallSession(
            string callId,
            string peerJid,
            string callCreatorJid,
            bool isVideo,
            bool inbound,
            ICallSignalingChannel signaling)
        {
            if (string.IsNullOrEmpty(callId))
            {
                throw new ArgumentException("call id is required", nameof(callId));
            }

            if (signaling == null)
            {
                throw new ArgumentNullException(nameof(signaling));
            }

            CallId = callId;
            PeerJid = peerJid;
            CallCreatorJid = string.IsNullOrEmpty(callCreatorJid) ? peerJid : callCreatorJid;
            IsVideo = isVideo;
            IsInbound = inbound;
            _signaling = signaling;
            Engine = new CallEngine(callId, inbound);
            State = inbound ? CallSessionState.Ringing : CallSessionState.Connecting;
        }

        public string CallId { get; }

        public string PeerJid { get; }

        public string CallCreatorJid { get; }

        public bool IsVideo { get; }

        public bool IsInbound { get; }

        /// <summary>Portable media coordinator (STUN/relay/MLow hooks). Not media-ready yet.</summary>
        public CallEngine Engine { get; }

        public CallSessionState State { get; private set; }

        /// <summary>
        /// Accepts an inbound ring on the wire (accept stanza). Media attach is still
        /// Phase 4 — state moves to Connecting after the stanza is sent.
        /// </summary>
        public async Task AnswerAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            lock (_gate)
            {
                EnsureNotEnded();
                if (!IsInbound || State != CallSessionState.Ringing)
                {
                    throw new InvalidOperationException(
                        "Answer is only valid for an inbound call in Ringing.");
                }
            }

            await _signaling.AcceptAsync(
                CallId,
                PeerJid,
                CallCreatorJid,
                IsVideo,
                cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                State = CallSessionState.Connecting;
                Engine.MarkConnecting();
            }
        }

        public async Task RejectAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            lock (_gate)
            {
                EnsureNotEnded();
                if (!IsInbound || State != CallSessionState.Ringing)
                {
                    throw new InvalidOperationException(
                        "Reject is only valid for an inbound call in Ringing.");
                }
            }

            await _signaling.RejectAsync(
                CallId,
                PeerJid,
                CallCreatorJid,
                cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                State = CallSessionState.Ended;
                Engine.Hangup();
            }
        }

        public async Task HangupAsync(
            string reason = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            lock (_gate)
            {
                EnsureNotEnded();
            }

            await _signaling.TerminateAsync(
                CallId,
                PeerJid,
                CallCreatorJid,
                reason,
                cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                State = CallSessionState.Ended;
                Engine.Hangup();
            }
        }

        public void MarkActive()
        {
            lock (_gate)
            {
                EnsureNotEnded();
                State = CallSessionState.Active;
                Engine.MarkActive();
            }
        }

        public void MarkEnded()
        {
            lock (_gate)
            {
                State = CallSessionState.Ended;
                Engine.Hangup();
            }
        }

        private void EnsureNotEnded()
        {
            if (State == CallSessionState.Ended)
            {
                throw new InvalidOperationException("Call already ended.");
            }
        }
    }
}
