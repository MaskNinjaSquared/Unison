using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HeldStatusPersistTests
    {
        private readonly List<IReadOnlyList<KeyValuePair<string, MessageSendState>>> _writes =
            new List<IReadOnlyList<KeyValuePair<string, MessageSendState>>>();
        private readonly TaskCompletionSource<bool> _timer = new TaskCompletionSource<bool>();
        private int _timersStarted;

        private HeldStatusPersist Create() =>
            new HeldStatusPersist(
                batch =>
                {
                    _writes.Add(batch);
                    return Task.FromResult(batch.Count);
                },
                TimeSpan.FromSeconds(2),
                _ =>
                {
                    _timersStarted++;
                    return _timer.Task;
                });

        [Fact]
        public async Task Receipts_coalesce_into_one_write_keeping_the_strongest()
        {
            HeldStatusPersist persist = Create();

            persist.Hold("A", ChatMessage.StatusDelivered);
            persist.Hold("A", ChatMessage.StatusRead);
            persist.Hold("A", ChatMessage.StatusDelivered);
            persist.Hold("B", ChatMessage.StatusSent);
            Assert.Empty(_writes);
            Assert.Equal(1, _timersStarted);

            await persist.FlushAsync();

            var batch = Assert.Single(_writes);
            var byId = new Dictionary<string, MessageSendState>();
            foreach (var pair in batch)
            {
                byId[pair.Key] = pair.Value;
            }

            Assert.Equal(MessageSendState.Read, byId["A"]);
            Assert.Equal(MessageSendState.Sent, byId["B"]);
        }

        [Fact]
        public async Task Pending_failed_and_unknown_statuses_are_not_written()
        {
            HeldStatusPersist persist = Create();

            persist.Hold("A", ChatMessage.StatusPending);
            persist.Hold("B", ChatMessage.StatusFailed);
            persist.Hold("C", "played-ish");
            persist.Hold(null, ChatMessage.StatusRead);

            await persist.FlushAsync();

            Assert.Empty(_writes);
            Assert.Equal(0, _timersStarted);
        }

        [Fact]
        public async Task A_receipt_after_a_flush_schedules_a_new_write()
        {
            HeldStatusPersist persist = Create();

            persist.Hold("A", ChatMessage.StatusRead);
            await persist.FlushAsync();
            persist.Hold("B", ChatMessage.StatusRead);

            Assert.Equal(2, _timersStarted);
        }
    }
}
