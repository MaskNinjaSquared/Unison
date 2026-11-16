// =============================================================================
// CallSession
//
// Lifecycle for one WhatsApp call (meowcaller Call). Signaling goes through
// ICallSignalingChannel; media I/O is reserved for the engine port (Phase 4).
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;

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
            State = inbound ? CallSessionState.Ringing : CallSessionState.Connecting;
        }

        public string CallId { get; }

        public string PeerJid { get; }

        public string CallCreatorJid { get; }

        public bool IsVideo { get; }

        public bool IsInbound { get; }

        public CallSessionState State { get; private set; }

        /// <summary>
        /// Accepts an inbound ring. Media/engine attach is not wired yet — state moves to
        /// Connecting so the façade can gate UI until Phase 4.
        /// </summary>
        public void Answer()
        {
            lock (_gate)
            {
                EnsureNotEnded();
                if (!IsInbound || State != CallSessionState.Ringing)
                {
                    throw new InvalidOperationException(
                        "Answer is only valid for an inbound call in Ringing.");
                }

                State = CallSessionState.Connecting;
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
            }
        }

        public void MarkActive()
        {
            lock (_gate)
            {
                EnsureNotEnded();
                State = CallSessionState.Active;
            }
        }

        public void MarkEnded()
        {
            lock (_gate)
            {
                State = CallSessionState.Ended;
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
