// =============================================================================
// CallEngine
//
// meowcaller engine/Call media lifecycle (netstandard2.0). Signaling stays on
// ICallSignalingChannel; this owns phase + hooks until MLow/IO are wired.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Engine.Relay;

namespace Unison.Voip.Engine
{
    public enum CallEnginePhase
    {
        Idle = 0,
        Ringing = 1,
        Connecting = 2,
        Active = 3,
        Ended = 4
    }

    /// <summary>
    /// Portable media coordinator for one call. Silence RTP can flow; mic/speaker
    /// I/O is not wired — <see cref="IsMediaReady"/> stays false until AudioGraph.
    /// </summary>
    public sealed class CallEngine
    {
        private readonly object _gate = new object();
        private bool _acceptPending;
        private bool _muteV2Seen;
        private string _relayHint;
        private CancellationTokenSource _mediaCts;
        private Task _mediaTask;
        private IRelayMediaPipe _mediaPipe;
        private RelayMediaOptions _mediaOptions;

        public CallEngine(string callId, bool inbound)
        {
            if (string.IsNullOrWhiteSpace(callId))
            {
                throw new ArgumentException("call id is required", nameof(callId));
            }

            CallId = callId.Trim();
            IsInbound = inbound;
            Phase = inbound ? CallEnginePhase.Ringing : CallEnginePhase.Connecting;
        }

        public string CallId { get; }

        public bool IsInbound { get; }

        public CallEnginePhase Phase { get; private set; }

        /// <summary>True after Answer while waiting for caller mute_v2 (meowcaller).</summary>
        public bool AcceptPending
        {
            get
            {
                lock (_gate)
                {
                    return _acceptPending;
                }
            }
        }

        public bool MuteV2Seen
        {
            get
            {
                lock (_gate)
                {
                    return _muteV2Seen;
                }
            }
        }

        public string RelayHint
        {
            get
            {
                lock (_gate)
                {
                    return _relayHint;
                }
            }
        }

        /// <summary>Real I/O not ported — hosts keep IsMediaAvailable false.</summary>
        public bool IsMediaReady => false;

        public void OnOfferMediaHints()
        {
            lock (_gate)
            {
                EnsureNotEnded();
                if (Phase == CallEnginePhase.Idle)
                {
                    Phase = CallEnginePhase.Ringing;
                }
            }
        }

        /// <summary>Local Answer: Connecting + acceptPending (accept stanza waits for mute_v2).</summary>
        public void MarkAnswerPending()
        {
            lock (_gate)
            {
                EnsureNotEnded();
                if (!IsInbound || Phase != CallEnginePhase.Ringing)
                {
                    throw new InvalidOperationException(
                        "Answer pending is only valid for an inbound call in Ringing.");
                }

                _acceptPending = true;
                Phase = CallEnginePhase.Connecting;
            }
        }

        /// <summary>
        /// Immediate-accept path (CallSession.AnswerAsync still sends accept now).
        /// Deferred hosts use <see cref="MarkAnswerPending"/> instead.
        /// </summary>
        public void MarkConnecting()
        {
            lock (_gate)
            {
                EnsureNotEnded();
                _acceptPending = false;
                Phase = CallEnginePhase.Connecting;
            }
        }

        /// <summary>Returns true once when the first mute_v2 should trigger deferred accept.</summary>
        public bool TryConsumeMuteV2ForAccept()
        {
            lock (_gate)
            {
                _muteV2Seen = true;
                if (!_acceptPending || Phase == CallEnginePhase.Ended)
                {
                    return false;
                }

                _acceptPending = false;
                return true;
            }
        }

        public void OnRelayHint(string hint)
        {
            lock (_gate)
            {
                EnsureNotEnded();
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    _relayHint = hint.Trim();
                }
            }
        }

        public void MarkActive()
        {
            lock (_gate)
            {
                EnsureNotEnded();
                Phase = CallEnginePhase.Active;
            }
        }

        public void Hangup()
        {
            StopRelayMedia();
            lock (_gate)
            {
                _acceptPending = false;
                Phase = CallEnginePhase.Ended;
            }
        }

        /// <summary>
        /// Starts the relay media loop over an already-open DataChannel. Without an
        /// encoder in <paramref name="options"/> it only holds the allocation.
        /// Idempotent: a second call is ignored while a loop is running.
        /// </summary>
        public void StartRelayMedia(
            IRelayMediaPipe pipe,
            RelayMediaConfig config,
            RelayMediaOptions options = null)
        {
            if (pipe == null)
            {
                throw new ArgumentNullException(nameof(pipe));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            lock (_gate)
            {
                EnsureNotEnded();
                if (_mediaTask != null)
                {
                    return;
                }

                _mediaPipe = pipe;
                _mediaOptions = options;
                _mediaCts = new CancellationTokenSource();
                CancellationToken ct = _mediaCts.Token;
                _mediaTask = Task.Run(() => RelayMediaLoop.RunAsync(pipe, config, options, ct));
            }
        }

        public void StopRelayMedia()
        {
            CancellationTokenSource cts;
            Task task;
            IRelayMediaPipe pipe;
            RelayMediaOptions options;
            lock (_gate)
            {
                cts = _mediaCts;
                task = _mediaTask;
                pipe = _mediaPipe;
                options = _mediaOptions;
                _mediaCts = null;
                _mediaTask = null;
                _mediaPipe = null;
                _mediaOptions = null;
            }

            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch
                {
                }

                cts.Dispose();
            }

            if (pipe != null)
            {
                try
                {
                    pipe.Dispose();
                }
                catch
                {
                }
            }

            if (task != null)
            {
                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch
                {
                }
            }

            DisposeAudio(options);
        }

        private static void DisposeAudio(RelayMediaOptions options)
        {
            if (options == null)
            {
                return;
            }

            try
            {
                if (options.Source != null)
                {
                    options.Source.Dispose();
                }
            }
            catch
            {
            }

            try
            {
                if (options.Sink != null)
                {
                    options.Sink.Dispose();
                }
            }
            catch
            {
            }
        }

        private void EnsureNotEnded()
        {
            if (Phase == CallEnginePhase.Ended)
            {
                throw new InvalidOperationException("Call engine already ended.");
            }
        }
    }
}
