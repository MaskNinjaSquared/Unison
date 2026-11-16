// =============================================================================
// CallFacade
//
// UI → CallSession → signaling channel. No WinRT; media gated by IsMediaAvailable.
// Tracks the one call the call screen shows, fed by live socket call statuses.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Unison.Voip.Abstractions;
using Unison.Voip.Session;

namespace Unison.WhatsAppClient.Calls
{
    public sealed class CallFacade : ICallService
    {
        private readonly ICallSignalingChannel _signaling;
        private readonly Func<string, bool> _isSelfJid;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<TimeSpan, Task> _delay;
        private readonly object _gate = new object();
        private ActiveCallSnapshot _active;

        public CallFacade(
            ICallSignalingChannel signaling,
            Func<string, bool> isSelfJid,
            Func<DateTime> utcNow = null,
            Func<TimeSpan, Task> delay = null)
        {
            _signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
            _isSelfJid = isSelfJid ?? throw new ArgumentNullException(nameof(isSelfJid));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _delay = delay ?? Task.Delay;
        }

        /// <summary>Engine media is not ported yet — keep UI “unavailable” for place/answer.</summary>
        public bool IsMediaAvailable => false;

        public ActiveCallSnapshot ActiveCall
        {
            get
            {
                lock (_gate)
                {
                    return _active;
                }
            }
        }

        public event EventHandler ActiveCallChanged;

        public void NotifyCallEvents(IReadOnlyList<CallEventNotifyRequest> events)
        {
            if (events == null)
            {
                return;
            }

            foreach (CallEventNotifyRequest callEvent in events)
            {
                Apply(callEvent);
            }
        }

        public async Task EndActiveCallAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            ActiveCallSnapshot ending;
            lock (_gate)
            {
                ending = _active;
                if (ending == null)
                {
                    return;
                }

                _active = null;
            }

            RaiseChanged();
            if (ending.Phase == ActiveCallPhase.Ringing)
            {
                await RejectAsync(ending.CallId, ending.PeerJid, ending.CallCreatorJid, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await HangupAsync(ending.CallId, ending.PeerJid, ending.CallCreatorJid, null, cancellationToken)
                .ConfigureAwait(false);
        }

        public void SetMuted(bool muted) => UpdateAudio(call => call.With(isMuted: muted));

        public void SetSpeakerOn(bool on) => UpdateAudio(call => call.With(isSpeakerOn: on));

        public void SetBluetoothOn(bool on) => UpdateAudio(call => call.With(isBluetoothOn: on));

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

        private void Apply(CallEventNotifyRequest callEvent)
        {
            if (callEvent == null)
            {
                return;
            }

            ActiveCallSnapshot rang = null;
            bool changed;
            lock (_gate)
            {
                ActiveCallEventAction action = ActiveCallEventPolicy.Decide(
                    callEvent,
                    _isSelfJid(callEvent.CallerJid),
                    _active,
                    _utcNow());
                switch (action)
                {
                    case ActiveCallEventAction.Ring:
                        string caller = string.IsNullOrWhiteSpace(callEvent.CallerJid)
                            ? callEvent.ChatJid
                            : callEvent.CallerJid;
                        _active = new ActiveCallSnapshot(
                            callEvent.CallId.Trim(),
                            string.IsNullOrWhiteSpace(callEvent.ChatJid) ? caller : callEvent.ChatJid,
                            caller,
                            isInbound: true,
                            isVideo: callEvent.IsVideo,
                            ActiveCallPhase.Ringing);
                        rang = _active;
                        changed = true;
                        break;
                    case ActiveCallEventAction.End:
                        _active = null;
                        changed = true;
                        break;
                    default:
                        changed = false;
                        break;
                }
            }

            if (changed)
            {
                RaiseChanged();
            }

            if (rang != null)
            {
                _ = DropUnansweredRingAsync(rang, callEvent.TimestampUtc);
            }
        }

        /// <summary>A ring whose end status never reaches us must not hold the screen forever.</summary>
        private async Task DropUnansweredRingAsync(ActiveCallSnapshot ringing, DateTime offeredAtUtc)
        {
            TimeSpan left = ActiveCallEventPolicy.RingFreshWindow - (_utcNow() - offeredAtUtc);
            if (left > TimeSpan.Zero)
            {
                await _delay(left).ConfigureAwait(false);
            }

            lock (_gate)
            {
                if (!ReferenceEquals(_active, ringing))
                {
                    return;
                }

                _active = null;
            }

            RaiseChanged();
        }

        private void UpdateAudio(Func<ActiveCallSnapshot, ActiveCallSnapshot> change)
        {
            lock (_gate)
            {
                if (_active == null ||
                    (_active.Phase != ActiveCallPhase.Dialing && _active.Phase != ActiveCallPhase.Active))
                {
                    return;
                }

                _active = change(_active);
            }

            RaiseChanged();
        }

        private void RaiseChanged() => ActiveCallChanged?.Invoke(this, EventArgs.Empty);
    }
}
