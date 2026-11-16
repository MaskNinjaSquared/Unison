using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public class CallScreenViewModelTests
    {
        private const string Peer = "5511988887777@s.whatsapp.net";
        private static readonly DateTime Now = new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);

        private static ActiveCallSnapshot Call(ActiveCallPhase phase, DateTime? connectedAt = null, bool muted = false) =>
            new ActiveCallSnapshot("c1", Peer, Peer, isInbound: true, isVideo: false, phase, connectedAt, isMuted: muted);

        private static (CallScreenViewModel vm, FakeCalls calls, ChatStateStore chats) Create()
        {
            var chats = new ChatStateStore(new InlineDispatcher());
            var calls = new FakeCalls();
            var vm = new CallScreenViewModel(calls, chats, new InlineDispatcher(), strings: null, utcNow: () => Now);
            return (vm, calls, chats);
        }

        [Fact]
        public void Closed_while_no_call_is_up()
        {
            var (vm, _, _) = Create();

            Assert.False(vm.IsOpen);
        }

        [Fact]
        public void Ringing_call_opens_with_label_name_and_number()
        {
            var (vm, calls, chats) = Create();
            chats.Chats.Add(new ChatItem { JID = Peer, Name = "Maria", AvatarUrl = "ms-appdata:///local/a.jpg" });

            calls.Publish(Call(ActiveCallPhase.Ringing));

            Assert.True(vm.IsOpen);
            Assert.Equal("RECEIVING CALL...", vm.StatusText);
            Assert.Equal("Maria", vm.DisplayName);
            Assert.Equal("+5511988887777", vm.PhoneNumber);
            Assert.Equal("ms-appdata:///local/a.jpg", vm.AvatarUrl);
            Assert.True(vm.HasAvatar);
        }

        [Fact]
        public void Unknown_peer_falls_back_to_the_number_and_placeholder()
        {
            var (vm, calls, _) = Create();

            calls.Publish(Call(ActiveCallPhase.Ringing));

            Assert.Equal("+5511988887777", vm.DisplayName);
            Assert.False(vm.HasAvatar);
        }

        [Fact]
        public void Connected_call_shows_the_running_time_on_refresh()
        {
            var (vm, calls, _) = Create();
            calls.Publish(Call(ActiveCallPhase.Active, Now.AddSeconds(-80)));

            vm.RefreshClock();

            Assert.Equal("01:20", vm.StatusText);
        }

        [Fact]
        public void Call_going_away_closes_the_screen()
        {
            var (vm, calls, _) = Create();
            calls.Publish(Call(ActiveCallPhase.Ringing));

            calls.Publish(null);

            Assert.False(vm.IsOpen);
        }

        [Fact]
        public void End_call_goes_through_the_facade()
        {
            var (vm, calls, _) = Create();
            calls.Publish(Call(ActiveCallPhase.Ringing));

            vm.EndCallCommand.Execute(null);

            Assert.Equal(1, calls.EndCount);
        }

        [Fact]
        public void Mute_toggles_from_the_current_state()
        {
            var (vm, calls, _) = Create();
            calls.Publish(Call(ActiveCallPhase.Active, Now, muted: true));

            vm.MuteCommand.Execute(null);

            Assert.False(calls.LastMuted);
        }

        [Fact]
        public void Message_asks_to_open_the_peer_chat()
        {
            var (vm, calls, _) = Create();
            calls.Publish(Call(ActiveCallPhase.Ringing));
            string opened = null;
            vm.OpenChatRequested += (_, jid) => opened = jid;

            vm.MessageCommand.Execute(null);

            Assert.Equal(Peer, opened);
        }

        private sealed class FakeCalls : ICallService
        {
            public ActiveCallSnapshot ActiveCall { get; private set; }
            public int EndCount;
            public bool? LastMuted;

            public event EventHandler ActiveCallChanged;

            public bool IsMediaAvailable => false;

            public void Publish(ActiveCallSnapshot call)
            {
                ActiveCall = call;
                ActiveCallChanged?.Invoke(this, EventArgs.Empty);
            }

            public Task EndActiveCallAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                EndCount++;
                return Task.CompletedTask;
            }

            public void SetMuted(bool muted) => LastMuted = muted;

            public void SetSpeakerOn(bool on)
            {
            }

            public void SetBluetoothOn(bool on)
            {
            }

            public void NotifyCallEvents(IReadOnlyList<CallEventNotifyRequest> events)
            {
            }

            public Task RejectAsync(string callId, string peerJid, string callCreatorJid, CancellationToken cancellationToken = default(CancellationToken)) =>
                Task.CompletedTask;

            public Task HangupAsync(string callId, string peerJid, string callCreatorJid, string reason = null, CancellationToken cancellationToken = default(CancellationToken)) =>
                Task.CompletedTask;
        }

        private sealed class InlineDispatcher : IDispatcher
        {
            public Task RunAsync(Action action, string caller = null, string callerFile = null)
            {
                action();
                return Task.CompletedTask;
            }
        }
    }
}
