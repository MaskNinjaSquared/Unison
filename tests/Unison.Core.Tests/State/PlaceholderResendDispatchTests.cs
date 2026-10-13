using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class PlaceholderResendDispatchTests
    {
        private const string Chat = "111@s.whatsapp.net";
        private static readonly TimeSpan Dispatch = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan Response = TimeSpan.FromSeconds(15);

        private readonly MissingMessageLedger _ledger = new MissingMessageLedger();
        private readonly FakeSocket _socket = new FakeSocket();
        private readonly TaskCompletionSource<bool> _responseWindow = new TaskCompletionSource<bool>();

        private PlaceholderResendDispatch NewDispatch(Func<TimeSpan, CancellationToken, Task> delay = null) =>
            new PlaceholderResendDispatch(
                _ledger,
                _socket,
                null,
                Dispatch,
                Response,
                delay ?? ((span, token) => span == Response ? _responseWindow.Task : Task.CompletedTask));

        [Fact]
        public async Task A_scheduled_resend_is_sent_once_and_counted()
        {
            MissingMessageCandidate candidate = Noted("m1", participant: "p@s.whatsapp.net");

            await NewDispatch().TryDispatch(candidate, Chat, "m1", "test");

            Assert.Equal(1, _socket.Sends);
            Assert.Equal("m1", _socket.LastKey.Id);
            Assert.Equal(Chat, _socket.LastKey.RemoteJid);
            Assert.Equal("p@s.whatsapp.net", _socket.LastKey.Participant);
            Assert.True(candidate.PlaceholderRequestInFlight);
            Assert.Equal(1, candidate.PlaceholderRequestCount);
            Assert.Equal("stanza-1", candidate.LastPlaceholderRequestId);
            Assert.Equal("test", _ledger.Resends["stanza-1"].Trigger);
        }

        [Fact]
        public void A_row_that_may_not_be_asked_yet_is_not_scheduled()
        {
            MissingMessageCandidate candidate = Noted("m1");
            candidate.PlaceholderRequestInFlight = true;

            Assert.Null(NewDispatch().TryDispatch(candidate, Chat, "m1", "test"));
            Assert.Equal(0, _socket.Sends);
        }

        [Fact]
        public async Task A_late_arrival_cancels_the_resend_before_it_is_sent()
        {
            MissingMessageCandidate candidate = Noted("m1");
            var gate = new TaskCompletionSource<bool>();
            PlaceholderResendDispatch dispatch = NewDispatch((span, token) =>
            {
                token.Register(() => gate.TrySetCanceled());
                return gate.Task;
            });

            Task run = dispatch.TryDispatch(candidate, Chat, "m1", "test");
            _ledger.Resolve(Chat, "m1").ScheduledResend.Cancel();
            await run;

            Assert.Equal(0, _socket.Sends);
        }

        [Fact]
        public async Task A_send_that_throws_gives_the_try_back()
        {
            MissingMessageCandidate candidate = Noted("m1");
            _socket.Fail = true;

            await NewDispatch().TryDispatch(candidate, Chat, "m1", "test");

            Assert.False(candidate.PlaceholderRequestInFlight);
            Assert.Equal(0, candidate.PlaceholderRequestCount);
            Assert.Null(candidate.LastPlaceholderRequestId);
            Assert.False(_ledger.Resends.ContainsKey("stanza-1"));
        }

        [Fact]
        public async Task An_unanswered_resend_stops_being_in_flight_after_the_window()
        {
            MissingMessageCandidate candidate = Noted("m1");

            await NewDispatch().TryDispatch(candidate, Chat, "m1", "test");
            Assert.True(candidate.PlaceholderRequestInFlight);

            _responseWindow.SetResult(true);
            for (int i = 0; i < 100 && _ledger.Resends.ContainsKey("stanza-1"); i++)
            {
                await Task.Delay(10);
            }

            Assert.False(_ledger.Resends.ContainsKey("stanza-1"));
            Assert.False(candidate.PlaceholderRequestInFlight);
            Assert.Equal(1, candidate.PlaceholderRequestCount);
        }

        [Fact]
        public async Task A_row_resolved_while_waiting_is_not_sent()
        {
            MissingMessageCandidate candidate = Noted("m1");
            var gate = new TaskCompletionSource<bool>();
            PlaceholderResendDispatch dispatch = NewDispatch((span, token) => gate.Task);

            Task run = dispatch.TryDispatch(candidate, Chat, "m1", "test");
            _ledger.Resolve(Chat, "m1");
            gate.SetResult(true);
            await run;

            Assert.Equal(0, _socket.Sends);
        }

        private MissingMessageCandidate Noted(string messageId, string participant = null)
        {
            DateTime now = DateTime.UtcNow;
            _ledger.Note(Chat, messageId, participant, false, now, "gap", now);
            MissingMessageCandidate candidate;
            _ledger.TryGet(Chat, messageId, out candidate);
            return candidate;
        }

        private sealed class FakeSocket : IPlaceholderResendSocket
        {
            private int _ids;

            public bool Fail { get; set; }
            public int Sends { get; private set; }
            public Proto.MessageKey LastKey { get; private set; }

            public string GenerateMessageId() => "stanza-" + Interlocked.Increment(ref _ids);

            public Task<string> RequestPlaceholderResendAsync(Proto.MessageKey key, string stanzaId)
            {
                if (Fail)
                {
                    throw new InvalidOperationException("socket closed");
                }

                Sends++;
                LastKey = key;
                return Task.FromResult(stanzaId);
            }
        }
    }
}
