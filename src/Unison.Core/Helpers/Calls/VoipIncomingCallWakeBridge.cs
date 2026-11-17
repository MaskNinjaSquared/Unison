// =============================================================================
// VoipIncomingCallWakeBridge
//
// Keeps VoipCallCoordinator in sync with ICallService.ActiveCall when the app
// window is hidden, and routes system Answer/Reject back to the call façade.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Calls
{
    public sealed class VoipIncomingCallWakeBridge
    {
        private readonly ICallService _calls;
        private readonly IVoipSystemCallHost _systemCalls;
        private readonly IAppVisibility _visibility;
        private readonly Func<ActiveCallSnapshot, string> _resolveContactName;
        private readonly Func<string, Task> _bringToForeground;
        private readonly Func<Task> _ensureProcessKeepAlive;
        private bool _started;

        public VoipIncomingCallWakeBridge(
            ICallService calls,
            IVoipSystemCallHost systemCalls,
            IAppVisibility visibility,
            Func<ActiveCallSnapshot, string> resolveContactName,
            Func<string, Task> bringToForeground,
            Func<Task> ensureProcessKeepAlive = null)
        {
            _calls = calls ?? throw new ArgumentNullException(nameof(calls));
            _systemCalls = systemCalls ?? throw new ArgumentNullException(nameof(systemCalls));
            _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
            _resolveContactName = resolveContactName ?? throw new ArgumentNullException(nameof(resolveContactName));
            _bringToForeground = bringToForeground ?? throw new ArgumentNullException(nameof(bringToForeground));
            _ensureProcessKeepAlive = ensureProcessKeepAlive;
        }

        public void Start()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _calls.ActiveCallChanged += (s, e) => _ = SyncAsync();
            _visibility.Changed += (s, e) => _ = SyncAsync();
            _systemCalls.AnswerRequested += OnAnswerRequested;
            _systemCalls.RejectRequested += OnRejectRequested;
            _ = SyncAsync();
        }

        private async Task SyncAsync()
        {
            if (!_systemCalls.IsAvailable)
            {
                return;
            }

            await _systemCalls.EnsureResourcesReservedAsync().ConfigureAwait(false);

            ActiveCallSnapshot call = _calls.ActiveCall;
            VoipSystemCallSyncAction action = VoipSystemCallWakePolicy.Decide(
                call,
                _systemCalls.CurrentCallId,
                _visibility.IsWindowVisible);

            if (action == VoipSystemCallSyncAction.End)
            {
                _systemCalls.NotifyEnded(_systemCalls.CurrentCallId);
                return;
            }

            if (action != VoipSystemCallSyncAction.Ring || call == null || call.IsVideo)
            {
                return;
            }

            // Unigram RS2: Location ExtendedExecution keeps the process (and socket) alive
            // while a call update is handled. Re-apply config so a mid-ring revoke is rare.
            if (_ensureProcessKeepAlive != null)
            {
                try
                {
                    await _ensureProcessKeepAlive().ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(_systemCalls.CurrentCallId) &&
                !string.Equals(_systemCalls.CurrentCallId, call.CallId, StringComparison.Ordinal))
            {
                _systemCalls.NotifyEnded(_systemCalls.CurrentCallId);
            }

            string name = _resolveContactName(call);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = call.PeerJid ?? call.CallId;
            }

            _systemCalls.RingIncoming(new VoipSystemIncomingRequest(
                call.CallId,
                name,
                call.PeerJid ?? string.Empty,
                call.IsVideo,
                ActiveCallEventPolicy.RingFreshWindow));
        }

        private void OnAnswerRequested(object sender, VoipSystemCallIdEventArgs e)
        {
            _ = AnswerFromSystemAsync(e?.CallId);
        }

        private async Task AnswerFromSystemAsync(string callId)
        {
            ActiveCallSnapshot active = _calls.ActiveCall;
            string resolvedId = !string.IsNullOrWhiteSpace(callId)
                ? callId
                : active?.CallId;

            if (active != null &&
                active.Phase == ActiveCallPhase.Ringing &&
                (string.IsNullOrWhiteSpace(callId) ||
                 string.Equals(active.CallId, callId, StringComparison.Ordinal)))
            {
                await _calls.AnswerActiveCallAsync().ConfigureAwait(false);
            }

            // Mobile: protocol LaunchUri foregrounds the app; Window.Activate alone does not.
            try
            {
                await _bringToForeground(resolvedId ?? string.Empty).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private void OnRejectRequested(object sender, VoipSystemCallIdEventArgs e)
        {
            _ = RejectFromSystemAsync(e?.CallId);
        }

        private async Task RejectFromSystemAsync(string callId)
        {
            ActiveCallSnapshot active = _calls.ActiveCall;
            if (active != null &&
                (string.IsNullOrWhiteSpace(callId) ||
                 string.Equals(active.CallId, callId, StringComparison.Ordinal)))
            {
                await _calls.EndActiveCallAsync().ConfigureAwait(false);
            }

            _systemCalls.NotifyEnded(callId);
        }
    }
}
