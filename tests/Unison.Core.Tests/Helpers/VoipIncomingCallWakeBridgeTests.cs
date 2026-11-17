using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class VoipIncomingCallWakeBridgeTests
    {
        [Fact]
        public async Task System_answer_answers_the_call_and_requests_foreground()
        {
            var calls = new FakeCalls();
            var system = new FakeSystemCalls();
            var visibility = new FakeVisibility { IsWindowVisible = false };
            string brought = null;
            var bridge = new VoipIncomingCallWakeBridge(
                calls,
                system,
                visibility,
                call => call.PeerJid,
                callId =>
                {
                    brought = callId;
                    return Task.CompletedTask;
                });
            bridge.Start();

            calls.Publish(new ActiveCallSnapshot(
                "c9",
                "5511@s.whatsapp.net",
                "5511@s.whatsapp.net",
                isInbound: true,
                isVideo: false,
                ActiveCallPhase.Ringing));
            system.RaiseAnswer("c9");
            await Task.Delay(50);

            Assert.Equal(1, calls.AnswerCount);
            Assert.Equal("c9", brought);
        }

        private sealed class FakeCalls : ICallService
        {
            public ActiveCallSnapshot ActiveCall { get; private set; }
            public int AnswerCount;

            public event EventHandler ActiveCallChanged;

            public bool IsMediaAvailable => false;

            public void Publish(ActiveCallSnapshot call)
            {
                ActiveCall = call;
                ActiveCallChanged?.Invoke(this, EventArgs.Empty);
            }

            public Task StartOutgoingAudioCallAsync(
                string peerJid,
                CancellationToken cancellationToken = default(CancellationToken)) =>
                Task.CompletedTask;

            public Task AnswerActiveCallAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                AnswerCount++;
                if (ActiveCall != null)
                {
                    ActiveCall = ActiveCall.With(phase: ActiveCallPhase.Connecting);
                    ActiveCallChanged?.Invoke(this, EventArgs.Empty);
                }

                return Task.CompletedTask;
            }

            public Task EndActiveCallAsync(CancellationToken cancellationToken = default(CancellationToken)) =>
                Task.CompletedTask;

            public void SetMuted(bool muted)
            {
            }

            public void SetSpeakerOn(bool on)
            {
            }

            public void SetBluetoothOn(bool on)
            {
            }

            public void NotifyCallEvents(IReadOnlyList<CallEventNotifyRequest> events)
            {
            }

            public Task RejectAsync(
                string callId,
                string peerJid,
                string callCreatorJid,
                CancellationToken cancellationToken = default(CancellationToken)) =>
                Task.CompletedTask;

            public Task HangupAsync(
                string callId,
                string peerJid,
                string callCreatorJid,
                string reason = null,
                CancellationToken cancellationToken = default(CancellationToken)) =>
                Task.CompletedTask;
        }

        private sealed class FakeSystemCalls : IVoipSystemCallHost
        {
            public bool IsAvailable => true;

            public string CurrentCallId { get; private set; }

            public event EventHandler<VoipSystemCallIdEventArgs> AnswerRequested;

            public event EventHandler<VoipSystemCallIdEventArgs> RejectRequested;

            public Task EnsureResourcesReservedAsync() => Task.CompletedTask;

            public void RingIncoming(VoipSystemIncomingRequest request)
            {
                CurrentCallId = request?.CallId;
            }

            public void NotifyEnded(string callId)
            {
                CurrentCallId = null;
            }

            public void RaiseAnswer(string callId) =>
                AnswerRequested?.Invoke(this, new VoipSystemCallIdEventArgs(callId));
        }

        private sealed class FakeVisibility : IAppVisibility
        {
            public bool IsWindowVisible { get; set; }

            public event EventHandler Changed;
        }
    }
}
