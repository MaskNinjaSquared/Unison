// =============================================================================
// MessageAckHandler — server acks for messages we sent (rc14 handleBadAck).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.Messages;
using Xunit;

namespace Unison.Core.Tests.Messages
{
    public sealed class MessageAckHandlerTests
    {
        [Fact]
        public async Task Error_ack_marks_the_message_failed_and_warns_with_the_code()
        {
            var bus = new RecordingBus();
            var log = new RecordingLog();
            var handler = new MessageAckHandler(bus, log);

            await handler.HandleAsync(Ack("message", "status@broadcast", "3EB0AA", error: "479"));

            var update = Assert.Single((IList<MessageUpdate>)Assert.Single(bus.Emitted).Payload);
            Assert.Equal(WaEventKind.MessagesUpdate, bus.Emitted[0].Kind);
            Assert.Equal("status@broadcast", update.RemoteJid);
            Assert.Equal("3EB0AA", update.MessageId);
            Assert.True(update.FromMe);
            Assert.Equal(ReceiptStatus.Error, update.Status);
            string warning = Assert.Single(log.Warnings);
            Assert.Contains("479", warning);
            Assert.Contains("3EB0AA", warning);
            Assert.Contains("smax-invalid", warning);
        }

        [Fact]
        public async Task Clean_ack_publishes_nothing()
        {
            var bus = new RecordingBus();
            var log = new RecordingLog();
            var handler = new MessageAckHandler(bus, log);

            await handler.HandleAsync(Ack("message", "status@broadcast", "3EB0AB", phash: "2:abc"));

            Assert.Empty(bus.Emitted);
            Assert.Empty(log.Warnings);
            Assert.Contains(log.Debugs, line => line.Contains("phash=2:abc"));
        }

        [Fact]
        public async Task Acks_of_other_classes_are_ignored()
        {
            var bus = new RecordingBus();
            var handler = new MessageAckHandler(bus, new RecordingLog());

            await handler.HandleAsync(Ack("receipt", "1@s.whatsapp.net", "X", error: "500"));

            Assert.Empty(bus.Emitted);
        }

        [Theory]
        [InlineData("400", "bad-request")]
        [InlineData("463", "account restricted")]
        [InlineData("479", "smax-invalid")]
        [InlineData("421", "unknown")]
        public void Known_codes_are_described(string code, string expected)
        {
            Assert.Contains(expected, MessageAckHandler.Describe(code));
        }

        [Fact]
        public async Task An_expected_send_is_told_the_refusal_code()
        {
            var handler = new MessageAckHandler(new RecordingBus(), new RecordingLog());

            using (SentAckWait wait = handler.Expect("3EB0AC"))
            {
                await handler.HandleAsync(Ack("message", "status@broadcast", "3EB0AC", error: "400"));

                Assert.Equal("400", await wait.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        [Fact]
        public async Task An_expected_send_the_server_took_is_not_a_refusal()
        {
            var handler = new MessageAckHandler(new RecordingBus(), new RecordingLog());

            using (SentAckWait wait = handler.Expect("3EB0AD"))
            {
                await handler.HandleAsync(Ack("message", "status@broadcast", "3EB0AD"));

                Assert.Null(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        [Fact]
        public async Task Silence_is_read_as_taken_rather_than_refused()
        {
            var handler = new MessageAckHandler(new RecordingBus(), new RecordingLog());

            using (SentAckWait wait = handler.Expect("3EB0AE"))
            {
                Assert.Null(await wait.WaitAsync(TimeSpan.FromMilliseconds(20)));
            }
        }

        [Fact]
        public async Task An_ack_for_another_message_leaves_the_wait_alone()
        {
            var handler = new MessageAckHandler(new RecordingBus(), new RecordingLog());

            using (SentAckWait wait = handler.Expect("3EB0AF"))
            {
                await handler.HandleAsync(Ack("message", "status@broadcast", "3EB0B0", error: "479"));

                Assert.Null(await wait.WaitAsync(TimeSpan.FromMilliseconds(20)));
            }
        }

        [Fact]
        public async Task A_disposed_wait_is_no_longer_answered()
        {
            var handler = new MessageAckHandler(new RecordingBus(), new RecordingLog());
            SentAckWait wait = handler.Expect("3EB0B1");
            wait.Dispose();

            await handler.HandleAsync(Ack("message", "status@broadcast", "3EB0B1", error: "400"));

            Assert.Null(await wait.WaitAsync(TimeSpan.FromMilliseconds(20)));
        }

        private static BinaryNode Ack(string cls, string from, string id, string error = null, string phash = null)
        {
            var attrs = new Dictionary<string, string> { { "class", cls }, { "from", from }, { "id", id } };
            if (error != null)
            {
                attrs["error"] = error;
            }

            if (phash != null)
            {
                attrs["phash"] = phash;
            }

            return new BinaryNode("ack", attrs);
        }

        private sealed class RecordingLog : ISocketLog
        {
            public List<string> Debugs { get; } = new List<string>();

            public List<string> Warnings { get; } = new List<string>();

            public void Trace(string message) { }

            public void Debug(string message) => Debugs.Add(message);

            public void Info(string message) => Debugs.Add(message);

            public void Warn(string message, Exception error = null) => Warnings.Add(message);

            public void Error(string message, Exception error = null) => Warnings.Add(message);
        }

        private sealed class RecordingBus : IWaEventBus
        {
            public List<(WaEventKind Kind, object Payload)> Emitted { get; } = new List<(WaEventKind, object)>();

            public bool IsBuffering => false;

            public Task<bool> EmitAsync(WaEventKind kind, object payload)
            {
                Emitted.Add((kind, payload));
                return Task.FromResult(false);
            }

            public IDisposable Process(Func<WaEventBatch, Task> handler) => null;

            public void Buffer() { }

            public Task<bool> FlushAsync() => Task.FromResult(false);

            public Task RunBufferedAsync(Func<Task> work) => work();

            public Task<T> RunBufferedAsync<T>(Func<Task<T>> work) => work();

            public void RegisterMerger(IWaEventMerger merger) { }

            public void Destroy() { }
        }
    }
}
