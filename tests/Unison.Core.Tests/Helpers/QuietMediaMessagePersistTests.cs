using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class QuietMediaMessagePersistTests
    {
        [Fact]
        public async Task Forwards_a_live_row_to_persist()
        {
            var outgoing = new FakeOutgoing();
            var quiet = new QuietMediaMessagePersist(outgoing);
            var message = new ChatMessage { Id = "M1", Status = ChatMessage.StatusSent };

            await quiet.SaveMessageQuietAsync("555@s.whatsapp.net", message);

            Assert.Equal(
                new[] { "persist:555@s.whatsapp.net:M1" },
                outgoing.Log);
        }

        [Fact]
        public async Task Store_failure_is_swallowed()
        {
            var outgoing = new FakeOutgoing { Fail = true };
            var quiet = new QuietMediaMessagePersist(outgoing);

            await quiet.SaveMessageQuietAsync(
                "555@s.whatsapp.net",
                new ChatMessage { Id = "M1" });

            Assert.Equal(1, outgoing.PersistCalls);
        }

        [Fact]
        public async Task Blank_chat_or_null_message_skips_persist()
        {
            var outgoing = new FakeOutgoing();
            var quiet = new QuietMediaMessagePersist(outgoing);

            await quiet.SaveMessageQuietAsync(" ", new ChatMessage { Id = "M1" });
            await quiet.SaveMessageQuietAsync("555@s.whatsapp.net", null);

            Assert.Empty(outgoing.Log);
        }

        private sealed class FakeOutgoing : IOutgoingMessageHost
        {
            public List<string> Log { get; } = new List<string>();

            public int PersistCalls { get; private set; }

            public bool Fail { get; set; }

            public Task EnsureConnectedAsync() => throw new NotSupportedException();

            public Task SendImageAsync(string jid, byte[] imageBytes, string caption, string messageId) =>
                throw new NotSupportedException();

            public Task SendAudioAsync(string jid, OutgoingAudio audio, string messageId) =>
                throw new NotSupportedException();

            public Task SendDocumentAsync(string jid, OutgoingDocument document, string messageId) =>
                throw new NotSupportedException();

            public Task PersistLiveAsync(string chatJid, ChatMessage message)
            {
                PersistCalls++;
                if (Fail)
                {
                    throw new InvalidOperationException("store down");
                }

                Log.Add("persist:" + chatJid + ":" + message.Id);
                return Task.CompletedTask;
            }

            public void QueueForPersist(string chatJid, ChatMessage message) =>
                throw new NotSupportedException();

            public Task<OutgoingSendAttempt> TrySendTextOnceAsync(OutgoingTextAttempt request) =>
                throw new NotSupportedException();

            public void InvalidateTransport(OutgoingSendAttempt attempt, string reason) =>
                throw new NotSupportedException();

            public void RecordSendAttempt(string messageId) => throw new NotSupportedException();

            public void RecordSendSuccess(string messageId) => throw new NotSupportedException();

            public void RecordSendFailure(string messageId, Exception error) =>
                throw new NotSupportedException();

            public void ReportError(Exception error) => throw new NotSupportedException();
        }
    }
}
