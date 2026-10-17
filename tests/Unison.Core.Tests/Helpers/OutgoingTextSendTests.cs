using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OutgoingTextSendTests
    {
        private static readonly DateTime At = new DateTime(2026, 10, 12, 9, 30, 0, DateTimeKind.Utc);

        private readonly FakeHost _host = new FakeHost();
        private readonly FakeJids _jids = new FakeJids();
        private readonly List<TimeSpan> _delays = new List<TimeSpan>();

        private OutgoingTextSend Create() => new OutgoingTextSend(
            _host,
            _jids,
            () => "3EB0ID",
            () => At,
            d => { _delays.Add(d); return Task.CompletedTask; });

        [Theory]
        [InlineData(null, "hi")]
        [InlineData(" ", "hi")]
        [InlineData("555@s.whatsapp.net", null)]
        [InlineData("555@s.whatsapp.net", "  ")]
        public async Task Blank_destination_or_text_is_rejected_before_anything_happens(string jid, string text)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Create().SendAsync(jid, text));

            Assert.Empty(_host.Log);
        }

        [Fact]
        public async Task Accepted_send_shows_the_bubble_before_the_transport_then_settles_it()
        {
            _host.Outcomes.Enqueue(Accepted());

            ChatMessage sent = await Create().SendAsync("555@s.whatsapp.net", "hello");

            Assert.Equal(
                new[]
                {
                    "attempt:3EB0ID",
                    "accept:555@s.whatsapp.net:pending",
                    "preview:555@s.whatsapp.net:Text",
                    "changed:555@s.whatsapp.net",
                    "persist-live:555@s.whatsapp.net:pending",
                    "send#1",
                    "success:3EB0ID",
                    "persist-live:555@s.whatsapp.net:sent",
                    "queue-persist:555@s.whatsapp.net",
                    "changed:555@s.whatsapp.net"
                },
                _host.Log);
            Assert.Equal(ChatMessage.StatusSent, sent.Status);
            Assert.Equal("3EB0ID", sent.Id);
            Assert.Equal(At, sent.Timestamp);
            Assert.Equal("111@s.whatsapp.net", sent.ParticipantJid);
        }

        [Fact]
        public async Task Bubble_lands_in_the_canonical_chat()
        {
            _jids.Canonical["999@lid"] = "555@s.whatsapp.net";
            _host.Outcomes.Enqueue(Accepted());

            ChatMessage sent = await Create().SendAsync("999@lid", "hello");

            Assert.Equal("555@s.whatsapp.net", sent.RemoteJid);
            Assert.Equal("555@s.whatsapp.net", _host.Requests[0].ChatJid);
        }

        [Fact]
        public async Task Self_chat_settles_as_read()
        {
            _jids.SelfLinked.Add("111@s.whatsapp.net");
            _host.Outcomes.Enqueue(Accepted());

            ChatMessage sent = await Create().SendAsync("111@s.whatsapp.net", "note to self");

            Assert.Equal(ChatMessage.StatusRead, sent.Status);
        }

        [Fact]
        public async Task A_receipt_that_arrived_during_the_send_is_not_walked_back()
        {
            _host.StatusAppliedOnAccept = ChatMessage.StatusDelivered;
            _host.Outcomes.Enqueue(Accepted());

            ChatMessage sent = await Create().SendAsync("555@s.whatsapp.net", "hello");

            Assert.Equal(ChatMessage.StatusDelivered, sent.Status);
        }

        [Fact]
        public async Task Transport_failure_drops_that_socket_and_retries_once_with_the_same_id()
        {
            var deadSocket = new object();
            _host.Outcomes.Enqueue(new OutgoingSendAttempt
            {
                Outcome = OutgoingSendAttemptOutcome.TransportFailure,
                Error = new IOException("pipe"),
                TransportToken = deadSocket
            });
            _host.Outcomes.Enqueue(Accepted());

            ChatMessage sent = await Create().SendAsync("555@s.whatsapp.net", "hello");

            Assert.Equal(ChatMessage.StatusSent, sent.Status);
            Assert.Same(deadSocket, _host.Invalidated[0].Token);
            Assert.Equal("send-attempt-1", _host.Invalidated[0].Reason);
            Assert.Equal(new[] { OutgoingTextSendRetry.RetryDelay }, _delays);
            Assert.Equal(2, _host.Requests.Count);
            Assert.All(_host.Requests, r => Assert.Equal("3EB0ID", r.MessageId));
            Assert.Equal(OutgoingTextSendRetry.ConnectTimeout(1), _host.Requests[0].ConnectTimeout);
            Assert.Equal(OutgoingTextSendRetry.ConnectTimeout(2), _host.Requests[1].ConnectTimeout);
            Assert.Equal(OutgoingTextSendRetry.WriteTimeout, _host.Requests[1].WriteTimeout);
            Assert.Equal(2, _host.Requests[1].Attempt);
        }

        [Fact]
        public async Task Refused_message_fails_after_one_attempt_and_keeps_the_connection()
        {
            var refusal = new InvalidOperationException("refused");
            _host.Outcomes.Enqueue(new OutgoingSendAttempt
            {
                Outcome = OutgoingSendAttemptOutcome.MessageRefused,
                Error = refusal
            });

            ChatMessage sent = await Create().SendAsync("555@s.whatsapp.net", "hello");

            Assert.Equal(ChatMessage.StatusFailed, sent.Status);
            Assert.Single(_host.Requests);
            Assert.Empty(_host.Invalidated);
            Assert.Empty(_delays);
            Assert.Same(refusal, _host.Failures[0]);
            Assert.Same(refusal, _host.Errors[0]);
            Assert.Contains("persist-live:555@s.whatsapp.net:failed", _host.Log);
            Assert.Equal("changed:555@s.whatsapp.net", _host.Log[_host.Log.Count - 1]);
        }

        [Fact]
        public async Task Socket_never_ready_fails_with_the_last_error_and_drops_nothing()
        {
            var notReady = new InvalidOperationException("WhatsApp socket is not ready");
            _host.Outcomes.Enqueue(new OutgoingSendAttempt { Outcome = OutgoingSendAttemptOutcome.SocketNotReady, Error = notReady });
            _host.Outcomes.Enqueue(new OutgoingSendAttempt { Outcome = OutgoingSendAttemptOutcome.SocketNotReady, Error = notReady });

            ChatMessage sent = await Create().SendAsync("555@s.whatsapp.net", "hello");

            Assert.Equal(ChatMessage.StatusFailed, sent.Status);
            Assert.Equal(2, _host.Requests.Count);
            Assert.Empty(_host.Invalidated);
            Assert.Same(notReady, _host.Errors[0]);
        }

        [Fact]
        public async Task A_failure_without_an_error_still_reports_one()
        {
            _host.Outcomes.Enqueue(new OutgoingSendAttempt { Outcome = OutgoingSendAttemptOutcome.MessageRefused });

            ChatMessage sent = await Create().SendAsync("555@s.whatsapp.net", "hello");

            Assert.Equal(ChatMessage.StatusFailed, sent.Status);
            Assert.IsType<InvalidOperationException>(_host.Errors[0]);
        }

        private static OutgoingSendAttempt Accepted() =>
            new OutgoingSendAttempt { Outcome = OutgoingSendAttemptOutcome.Accepted };

        private sealed class FakeHost : IOutgoingMessageHost
        {
            public List<string> Log { get; } = new List<string>();
            public Queue<OutgoingSendAttempt> Outcomes { get; } = new Queue<OutgoingSendAttempt>();
            public List<OutgoingTextAttempt> Requests { get; } = new List<OutgoingTextAttempt>();
            public List<(object Token, string Reason)> Invalidated { get; } = new List<(object, string)>();
            public List<Exception> Failures { get; } = new List<Exception>();
            public List<Exception> Errors { get; } = new List<Exception>();
            public string StatusAppliedOnAccept { get; set; }

            public Task EnsureConnectedAsync() => throw new NotSupportedException();

            public Task SendImageAsync(string jid, byte[] imageBytes, string caption, string messageId) => throw new NotSupportedException();

            public Task SendAudioAsync(string jid, OutgoingAudio audio, string messageId) => throw new NotSupportedException();

            public Task SendDocumentAsync(string jid, OutgoingDocument document, string messageId) =>
                throw new NotSupportedException();

            public void AcceptOutgoing(string chatJid, ChatMessage message)
            {
                Log.Add("accept:" + chatJid + ":" + message.Status);
                if (StatusAppliedOnAccept != null)
                {
                    message.Status = StatusAppliedOnAccept;
                }
            }

            public Task ShowLocalSendPreviewAsync(string chatJid, ChatMessage message, ChatPreviewKind kind)
            {
                Log.Add("preview:" + chatJid + ":" + kind);
                return Task.CompletedTask;
            }

            public void NotifyChatChanged(string chatJid) => Log.Add("changed:" + chatJid);

            public Task PersistLiveAsync(string chatJid, ChatMessage message)
            {
                Log.Add("persist-live:" + chatJid + ":" + message.Status);
                return Task.CompletedTask;
            }

            public void QueueForPersist(string chatJid, ChatMessage message) => Log.Add("queue-persist:" + chatJid);

            public Task<OutgoingSendAttempt> TrySendTextOnceAsync(OutgoingTextAttempt request)
            {
                Requests.Add(request);
                Log.Add("send#" + request.Attempt);
                return Task.FromResult(Outcomes.Dequeue());
            }

            public void InvalidateTransport(OutgoingSendAttempt attempt, string reason)
            {
                Invalidated.Add((attempt.TransportToken, reason));
                Log.Add("invalidate:" + reason);
            }

            public void RecordSendAttempt(string messageId) => Log.Add("attempt:" + messageId);

            public void RecordSendSuccess(string messageId) => Log.Add("success:" + messageId);

            public void RecordSendFailure(string messageId, Exception error)
            {
                Failures.Add(error);
                Log.Add("failure:" + messageId);
            }

            public void ReportError(Exception error)
            {
                Errors.Add(error);
                Log.Add("error");
            }

            public Task<List<ChatMessage>> LoadLiveOverlayAsync(string chatJid) =>
                throw new NotSupportedException();
        }

        private sealed class FakeJids : IJidResolver
        {
            public Dictionary<string, string> Canonical { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> SelfLinked { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public Profile Self { get; } = new Profile { Id = "111@s.whatsapp.net" };

            public string GetCanonicalJid(string jid)
            {
                string value;
                return jid != null && Canonical.TryGetValue(jid, out value) ? value : jid;
            }

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => jid != null && SelfLinked.Contains(jid);
        }
    }
}
