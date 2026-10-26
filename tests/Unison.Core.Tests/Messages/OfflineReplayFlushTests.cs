using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Messages;
using Xunit;

namespace Unison.Core.Tests.Messages
{
    public class OfflineReplayFlushTests
    {
        [Fact]
        public void Enqueue_below_threshold_schedules_timer_not_flush_claim()
        {
            var queue = new PendingMessageQueue(
                flushMessageThreshold: 12,
                flushInterval: TimeSpan.FromMilliseconds(750),
                maxMessagesPerChatBatch: 1500);
            var io = new RecordingIoHost();
            var flush = new OfflineReplayFlush(queue, io);

            flush.Enqueue(
                "1@s.whatsapp.net",
                new[] { new ChatMessage { Id = "m1", Content = "hi" } },
                DateTime.UtcNow,
                scheduleFlush: true);

            Assert.Equal(1, flush.PendingCount);
            Assert.False(flush.IsFlushClaimed);
            Assert.Equal(0, io.PersistCalls);
        }

        [Fact]
        public async Task Flush_drains_queue_and_persists_via_io_host()
        {
            var queue = new PendingMessageQueue(
                flushMessageThreshold: 12,
                flushInterval: TimeSpan.FromMilliseconds(750),
                maxMessagesPerChatBatch: 1500);
            var io = new RecordingIoHost();
            var flush = new OfflineReplayFlush(queue, io);

            flush.Enqueue(
                "1@s.whatsapp.net",
                new[] { new ChatMessage { Id = "m1", Content = "hi" } },
                DateTime.UtcNow,
                scheduleFlush: false);

            await flush.FlushOfflineReplayAsync("test");

            Assert.Equal(0, flush.PendingCount);
            Assert.Equal(1, io.PersistCalls);
            Assert.Equal(1, io.CatalogPersistCalls);
            Assert.Equal(1, io.LogFlushedCalls);
        }

        private sealed class RecordingIoHost : IOfflineReplayFlushIoHost
        {
            public int PersistCalls { get; private set; }
            public int CatalogPersistCalls { get; private set; }
            public int LogFlushedCalls { get; private set; }

            public Task PersistLiveMessagesAsync(string chatJid, IList<ChatMessage> messages)
            {
                PersistCalls++;
                return Task.CompletedTask;
            }

            public Task RemovePendingOutgoingAsync(IEnumerable<string> ids) =>
                Task.CompletedTask;

            public Task RemovePendingIncomingAsync(IEnumerable<string> ids) =>
                Task.CompletedTask;

            public void ScheduleCatalogPersist() => CatalogPersistCalls++;

            public void LogFlushed(int saved, int chatCount, int dirtyChatCount, string reason) =>
                LogFlushedCalls++;

            public void RecordFlushDeferred(Exception ex, string reason, int chatCount)
            {
            }

            public void LogNonFatalIdleFlushFailure(Exception ex)
            {
            }
        }
    }
}
