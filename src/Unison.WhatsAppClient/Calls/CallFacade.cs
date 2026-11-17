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
using Unison.Core.Helpers;
using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using System.Net;
using Unison.Voip.Abstractions;
using Unison.Voip.Engine;
using Unison.Voip.Engine.Mlow;
using Unison.Voip.Engine.Relay;
using Unison.Voip.Session;

namespace Unison.WhatsAppClient.Calls
{
    public sealed class CallFacade : ICallService
    {
        private readonly ICallSignalingChannel _signaling;
        private readonly Func<string, bool> _isSelfJid;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<TimeSpan, Task> _delay;
        private readonly Action<string> _log;
        private readonly ICallIoFactory _io;
        private readonly Func<string> _selfLid;
        private readonly object _gate = new object();
        private ActiveCallSnapshot _active;
        /// <summary>meowcaller acceptPending — accept stanza waits for caller mute_v2.</summary>
        private string _acceptPendingCallId;
        /// <summary>callKey + relay for <see cref="_active"/>; stale once _active changes call.</summary>
        private CallMediaHints _activeHints;
        private CallEngine _engine;

        public CallFacade(
            ICallSignalingChannel signaling,
            Func<string, bool> isSelfJid,
            Func<DateTime> utcNow = null,
            Func<TimeSpan, Task> delay = null,
            Action<string> log = null,
            ICallIoFactory ioFactory = null,
            Func<string> selfLid = null)
        {
            _signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
            _isSelfJid = isSelfJid ?? throw new ArgumentNullException(nameof(isSelfJid));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _delay = delay ?? Task.Delay;
            _log = log;
            _io = ioFactory;
            _selfLid = selfLid;
        }

        /// <summary>Mic/speaker not wired yet — keep UI “unavailable” for place/answer.</summary>
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

        /// <summary>Media material for the active call; null for any other call or none yet.</summary>
        public CallMediaHints GetMediaHints(string callId)
        {
            lock (_gate)
            {
                return IsActiveCall(callId) ? _activeHints : null;
            }
        }

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

        public async Task StartOutgoingAudioCallAsync(
            string peerJid,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(peerJid))
            {
                throw new ArgumentException("peerJid is required", nameof(peerJid));
            }

            lock (_gate)
            {
                if (_active != null && _active.Phase != ActiveCallPhase.Ended)
                {
                    throw new InvalidOperationException("A call is already active.");
                }
            }

            Note("offer begin peer=" + peerJid.Trim());
            CallOfferWireResult offer = await _signaling
                .OfferAudioAsync(peerJid.Trim(), cancellationToken)
                .ConfigureAwait(false);
            if (offer == null || string.IsNullOrWhiteSpace(offer.CallId))
            {
                throw new InvalidOperationException("Call offer returned no call id.");
            }

            string peer = !string.IsNullOrWhiteSpace(offer.PeerJid) ? offer.PeerJid : peerJid.Trim();
            string creator = !string.IsNullOrWhiteSpace(offer.CallCreatorJid)
                ? offer.CallCreatorJid
                : (_selfLid != null ? _selfLid() : null);

            ActiveCallSnapshot dialing;
            CallEngine stop;
            lock (_gate)
            {
                if (_active != null && _active.Phase != ActiveCallPhase.Ended)
                {
                    throw new InvalidOperationException("A call is already active.");
                }

                _active = new ActiveCallSnapshot(
                    offer.CallId.Trim(),
                    peer,
                    creator ?? peer,
                    isInbound: false,
                    isVideo: false,
                    ActiveCallPhase.Dialing);
                _acceptPendingCallId = null;
                _activeHints = offer.CallKey != null
                    ? new CallMediaHints { CallKey = offer.CallKey }
                    : null;
                stop = _engine;
                _engine = new CallEngine(offer.CallId.Trim(), inbound: false);
                dialing = _active;
            }

            HangupEngine(stop);
            Note("offer dialing callId=" + dialing.CallId + " peer=" + peer);
            RaiseChanged();
            _ = DropUnansweredRingAsync(dialing, _utcNow());
        }

        public async Task EndActiveCallAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            ActiveCallSnapshot ending;
            lock (_gate)
            {
                ending = _active;
                if (ending == null)
                {
                    Note("end skipped — no active call");
                    return;
                }
            }

            string toJid = WireTo(ending);
            string creator = WireCreator(ending);

            // Signal first, then clear — so a failed RequireConnection does not look like a hang-up.
            Note(
                "end begin callId=" + ending.CallId +
                " phase=" + ending.Phase +
                " to=" + toJid +
                " creator=" + creator);

            try
            {
                if (ending.Phase == ActiveCallPhase.Ringing)
                {
                    await RejectAsync(ending.CallId, toJid, creator, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // meowcaller hangup: terminate whether or not deferred accept already went out.
                    await HangupAsync(ending.CallId, toJid, creator, null, cancellationToken)
                        .ConfigureAwait(false);
                }

                Note("end signaled callId=" + ending.CallId);
            }
            catch (Exception ex)
            {
                Note("end failed callId=" + ending.CallId + " — " + ex.Message);
            }
            finally
            {
                lock (_gate)
                {
                    if (string.Equals(_acceptPendingCallId, ending.CallId, StringComparison.Ordinal))
                    {
                        _acceptPendingCallId = null;
                    }

                    if (_active != null &&
                        string.Equals(_active.CallId, ending.CallId, StringComparison.Ordinal))
                    {
                        _active = null;
                        _activeHints = null;
                    }
                }

                StopEngine();
                RaiseChanged();
            }
        }

        public Task AnswerActiveCallAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            // meowcaller answer: phase → Connecting + acceptPending; accept stanza waits for mute_v2.
            lock (_gate)
            {
                ActiveCallSnapshot ringing = _active;
                if (ringing == null ||
                    !ringing.IsInbound ||
                    ringing.Phase != ActiveCallPhase.Ringing)
                {
                    return Task.CompletedTask;
                }

                _acceptPendingCallId = ringing.CallId;
                _active = ringing.With(phase: ActiveCallPhase.Connecting);
            }

            Note("answer pending callId=" + _acceptPendingCallId + " (await mute_v2 for accept)");
            RaiseChanged();
            KickRelayMedia();
            return Task.CompletedTask;
        }

        public void SetMuted(bool muted) =>
            UpdateAudio(call => call.With(isMuted: muted), mediaControlsOnly: true);

        public void SetSpeakerOn(bool on) =>
            UpdateAudio(call => call.With(isSpeakerOn: on), mediaControlsOnly: false);

        public void SetBluetoothOn(bool on) =>
            UpdateAudio(call => call.With(isBluetoothOn: on), mediaControlsOnly: true);

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

            if (callEvent.IsSignalingControl || CallSignalingControl.IsControlTag(callEvent.ActionTag))
            {
                ApplyControl(callEvent);
                return;
            }

            ActiveCallSnapshot rang = null;
            bool changed;
            bool actionWasConnect = false;
            CallEngine stop = null;
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
                        string peer = string.IsNullOrWhiteSpace(callEvent.ChatJid)
                            ? callEvent.CallerJid
                            : callEvent.ChatJid;
                        // Reject stanza needs a real call-creator, not the @call relay outer from.
                        string creator = string.IsNullOrWhiteSpace(callEvent.CallerJid) ||
                                         JidHelper.IsCallRelayJid(callEvent.CallerJid)
                            ? peer
                            : callEvent.CallerJid;
                        if (string.IsNullOrWhiteSpace(peer))
                        {
                            peer = creator;
                        }

                        string wireTo = string.IsNullOrWhiteSpace(callEvent.SignalingToJid)
                            ? peer
                            : callEvent.SignalingToJid;

                        _active = new ActiveCallSnapshot(
                            callEvent.CallId.Trim(),
                            peer,
                            creator,
                            isInbound: true,
                            isVideo: callEvent.IsVideo,
                            ActiveCallPhase.Ringing,
                            signalingToJid: wireTo);
                        _acceptPendingCallId = null;
                        _activeHints = null;
                        stop = _engine;
                        _engine = new CallEngine(callEvent.CallId.Trim(), inbound: true);
                        MergeHints(callEvent.MediaHints);
                        rang = _active;
                        changed = true;
                        break;
                    case ActiveCallEventAction.Connect:
                        // Peer accepted our outbound offer — media path + Active timer.
                        _active = _active.With(
                            phase: ActiveCallPhase.Active,
                            connectedAtUtc: _utcNow());
                        MergeHints(callEvent.MediaHints);
                        actionWasConnect = true;
                        changed = true;
                        break;
                    case ActiveCallEventAction.End:
                        _active = null;
                        _acceptPendingCallId = null;
                        _activeHints = null;
                        stop = _engine;
                        _engine = null;
                        changed = true;
                        break;
                    default:
                        changed = false;
                        break;
                }
            }

            HangupEngine(stop);

            if (changed)
            {
                RaiseChanged();
            }

            if (actionWasConnect)
            {
                KickRelayMedia();
            }

            if (rang != null)
            {
                KickPreaccept(rang);
                _ = DropUnansweredRingAsync(rang, callEvent.TimestampUtc);
            }
        }

        /// <summary>mute_v2 → deferred accept; relaylatency → echo probes; any → merge relay.</summary>
        private void ApplyControl(CallEventNotifyRequest callEvent)
        {
            ActiveCallSnapshot target = null;
            lock (_gate)
            {
                if (IsActiveCall(callEvent.CallId))
                {
                    MergeHints(callEvent.MediaHints);
                    target = _active;
                }
            }

            if (string.Equals(callEvent.ActionTag, "mute_v2", StringComparison.OrdinalIgnoreCase))
            {
                _ = TrySendDeferredAcceptAsync(callEvent.CallId);
                return;
            }

            if (target != null &&
                target.IsInbound &&
                string.Equals(callEvent.ActionTag, "relaylatency", StringComparison.OrdinalIgnoreCase) &&
                callEvent.MediaHints != null)
            {
                foreach (CallRelayLatencyCandidate probe in callEvent.MediaHints.LatencyProbes)
                {
                    KickRelayLatency(target, probe);
                }
            }
        }

        private void KickRelayLatency(ActiveCallSnapshot call, CallRelayLatencyCandidate probe)
        {
            if (probe == null)
            {
                return;
            }

            try
            {
                Task task = _signaling.SendRelayLatencyAsync(
                    call.CallId,
                    WireTo(call),
                    WireCreator(call),
                    probe.LatencyMs,
                    probe.RelayName,
                    probe.AddressBytes,
                    CancellationToken.None);
                if (task != null && !task.IsCompleted)
                {
                    _ = ObserveSignalAsync(task, call.CallId);
                }
            }
            catch (Exception ex)
            {
                Note("relaylatency failed callId=" + call.CallId + " relay=" + probe.RelayName + " — " + ex.Message);
            }
        }

        /// <summary>Caller holds _gate. Later stanzas refresh the relay; the offer's callKey stays.</summary>
        private void MergeHints(CallMediaHints incoming)
        {
            if (incoming == null)
            {
                return;
            }

            if (_activeHints == null)
            {
                _activeHints = new CallMediaHints();
            }

            if (incoming.CallKey != null)
            {
                _activeHints.CallKey = incoming.CallKey;
            }

            if (incoming.RelayKey != null)
            {
                _activeHints.RelayKey = incoming.RelayKey;
            }

            if (incoming.RelayTokens != null && incoming.RelayTokens.Count > 0)
            {
                _activeHints.RelayTokens = incoming.RelayTokens;
            }

            if (incoming.RelayEndpoints != null && incoming.RelayEndpoints.Count > 0)
            {
                _activeHints.RelayEndpoints = incoming.RelayEndpoints;
            }

            if (!string.IsNullOrWhiteSpace(incoming.RelayPeerJid))
            {
                _activeHints.RelayPeerJid = incoming.RelayPeerJid;
            }
        }

        /// <summary>Caller holds _gate.</summary>
        private bool IsActiveCall(string callId) =>
            _active != null &&
            !string.IsNullOrWhiteSpace(callId) &&
            string.Equals(_active.CallId, callId.Trim(), StringComparison.Ordinal);

        private async Task TrySendDeferredAcceptAsync(string callId)
        {
            ActiveCallSnapshot pending;
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(_acceptPendingCallId) ||
                    _active == null ||
                    !string.Equals(_acceptPendingCallId, _active.CallId, StringComparison.Ordinal) ||
                    (!string.IsNullOrWhiteSpace(callId) &&
                     !string.Equals(_acceptPendingCallId, callId, StringComparison.Ordinal)))
                {
                    Note("mute_v2 ignored — no acceptPending" +
                         (string.IsNullOrWhiteSpace(callId) ? string.Empty : " callId=" + callId));
                    return;
                }

                pending = _active;
                _acceptPendingCallId = null;
            }

            Note("mute_v2 → deferred accept callId=" + pending.CallId + " to=" + WireTo(pending));
            try
            {
                await _signaling.AcceptAsync(
                    pending.CallId,
                    WireTo(pending),
                    WireCreator(pending),
                    pending.IsVideo,
                    CancellationToken.None).ConfigureAwait(false);
                Note("deferred accept sent callId=" + pending.CallId);
            }
            catch (Exception ex)
            {
                Note("deferred accept failed callId=" + pending.CallId + " — " + ex.Message);
            }
        }

        /// <summary>meowcaller sendPreaccept — must not block the UI; sync completions finish inline.</summary>
        private void KickPreaccept(ActiveCallSnapshot ringing)
        {
            if (ringing == null)
            {
                return;
            }

            try
            {
                Task task = _signaling.PreacceptAsync(
                    ringing.CallId,
                    WireTo(ringing),
                    WireCreator(ringing),
                    CancellationToken.None);
                if (task == null || task.IsCompleted)
                {
                    return;
                }

                _ = ObserveSignalAsync(task, ringing.CallId);
            }
            catch
            {
            }
        }

        private async Task ObserveSignalAsync(Task task, string callId)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
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

            CallEngine stop = null;
            lock (_gate)
            {
                if (!ReferenceEquals(_active, ringing))
                {
                    return;
                }

                _active = null;
                _activeHints = null;
                stop = _engine;
                _engine = null;
            }

            HangupEngine(stop);
            RaiseChanged();
        }

        private void KickRelayMedia()
        {
            _ = RunRelayMediaAsync();
        }

        private async Task RunRelayMediaAsync()
        {
            CallEngine engine;
            CallMediaHints hints;
            ActiveCallSnapshot active;
            lock (_gate)
            {
                engine = _engine;
                hints = _activeHints;
                active = _active;
            }

            if (engine == null || active == null || _io == null)
            {
                Note("relay media skipped — no engine/io");
                return;
            }

            string selfLid = _selfLid != null ? _selfLid() : null;
            RelayMediaConfig config = RelayMediaConfigMapper.From(
                hints, active.CallId, selfLid, WireTo(active), inbound: active.IsInbound);
            if (config == null || config.RelayKey == null || config.Endpoints.Count == 0)
            {
                Note("relay media skipped — missing relay hints callId=" + active.CallId);
                return;
            }

            RelayMediaEndpoint endpoint = RelayMediaEndpointSelector.Select(
                config.Endpoints, config.Inbound);
            IPEndPoint remote = RelayMediaLoop.ToEndPoint(endpoint);
            if (remote == null)
            {
                Note("relay media skipped — bad endpoint callId=" + active.CallId);
                return;
            }

            ICallUdpTransport udp = null;
            ICallAudioSource source = null;
            ICallAudioSink sink = null;
            try
            {
                udp = _io.CreateUdpTransport();
                Note("relay media connect callId=" + active.CallId + " to=" + remote);
                RelayMediaChannel channel = await RelayMediaChannel
                    .ConnectAsync(udp, remote, CancellationToken.None, ownsUdp: true)
                    .ConfigureAwait(false);
                udp = null; // owned by channel

                source = await StartAudioSourceAsync().ConfigureAwait(false);
                sink = await StartAudioSinkAsync().ConfigureAwait(false);

                ICallAudioSource capture = source;
                engine.StartRelayMedia(channel, config, new RelayMediaOptions
                {
                    Encoder = new MlowEncoder(),
                    Decoder = sink != null ? new MlowDecoder() : null,
                    Source = source,
                    Sink = sink,
                    NextFrame = () => IsActiveMuted() ? null : (capture != null ? capture.TryReadFrame() : null)
                });
                source = null; // owned by CallEngine via options
                sink = null;
                Note("relay media loop started callId=" + active.CallId);
            }
            catch (Exception ex)
            {
                Note("relay media failed callId=" + active.CallId + " — " + ex.Message);
                if (udp != null)
                {
                    try
                    {
                        udp.Dispose();
                    }
                    catch
                    {
                    }
                }

                try
                {
                    source?.Dispose();
                }
                catch
                {
                }

                try
                {
                    sink?.Dispose();
                }
                catch
                {
                }
            }
        }

        private async Task<ICallAudioSource> StartAudioSourceAsync()
        {
            ICallAudioSource source = _io.CreateAudioSource();
            try
            {
                await source.StartAsync(CancellationToken.None).ConfigureAwait(false);
                return source;
            }
            catch (Exception ex)
            {
                Note("call capture unavailable — " + ex.Message);
                try
                {
                    source.Dispose();
                }
                catch
                {
                }

                return null;
            }
        }

        private async Task<ICallAudioSink> StartAudioSinkAsync()
        {
            ICallAudioSink sink = _io.CreateAudioSink();
            try
            {
                await sink.StartAsync(CancellationToken.None).ConfigureAwait(false);
                return sink;
            }
            catch (Exception ex)
            {
                Note("call playback unavailable — " + ex.Message);
                try
                {
                    sink.Dispose();
                }
                catch
                {
                }

                return null;
            }
        }

        private bool IsActiveMuted()
        {
            lock (_gate)
            {
                return _active != null && _active.IsMuted;
            }
        }

        private void StopEngine()
        {
            CallEngine engine;
            lock (_gate)
            {
                engine = _engine;
                _engine = null;
            }

            HangupEngine(engine);
        }

        private static void HangupEngine(CallEngine engine)
        {
            if (engine == null)
            {
                return;
            }

            try
            {
                engine.Hangup();
            }
            catch
            {
            }
        }

        private void UpdateAudio(
            Func<ActiveCallSnapshot, ActiveCallSnapshot> change,
            bool mediaControlsOnly)
        {
            lock (_gate)
            {
                if (_active == null)
                {
                    return;
                }

                ActiveCallPhase phase = _active.Phase;
                bool allowed = mediaControlsOnly
                    ? phase == ActiveCallPhase.Dialing || phase == ActiveCallPhase.Active
                    : phase == ActiveCallPhase.Ringing ||
                      phase == ActiveCallPhase.Connecting ||
                      phase == ActiveCallPhase.Dialing ||
                      phase == ActiveCallPhase.Active;
                if (!allowed)
                {
                    return;
                }

                _active = change(_active);
            }

            RaiseChanged();
        }

        private void RaiseChanged() => ActiveCallChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>meowcaller <c>m.from</c> for stanza <c>to=</c>.</summary>
        private static string WireTo(ActiveCallSnapshot call) =>
            call == null
                ? null
                : (!string.IsNullOrWhiteSpace(call.SignalingToJid)
                    ? call.SignalingToJid
                    : call.PeerJid);

        private static string WireCreator(ActiveCallSnapshot call)
        {
            if (call == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(call.CallCreatorJid) ||
                JidHelper.IsCallRelayJid(call.CallCreatorJid))
            {
                return call.PeerJid;
            }

            return call.CallCreatorJid;
        }

        private void Note(string detail)
        {
            if (_log == null || string.IsNullOrEmpty(detail))
            {
                return;
            }

            try
            {
                _log("[Call] " + detail);
            }
            catch
            {
                // Diagnostics must never break hang-up / answer.
            }
        }
    }
}
