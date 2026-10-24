using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OfflineReplayFlushDispatchTests
    {
        [Fact]
        public void Collect_splits_outgoing_and_incoming_ids()
        {
            var outgoing = new HashSet<string>(StringComparer.Ordinal);
            var incoming = new HashSet<string>(StringComparer.Ordinal);

            OfflineReplayFlushBatch.CollectPendingIds(
                new[]
                {
                    new ChatMessage { Id = "o1", IsFromMe = true },
                    new ChatMessage { Id = "i1", IsFromMe = false },
                    new ChatMessage { Id = " ", IsFromMe = true },
                    null
                },
                outgoing,
                incoming);

            Assert.Equal(new[] { "o1" }, outgoing.OrderBy(x => x));
            Assert.Equal(new[] { "i1" }, incoming.OrderBy(x => x));
        }

        [Fact]
        public void ApplyAction_flush_now_starts_flush()
        {
            var host = new FakeHost();

            OfflineReplayFlushDispatch.ApplyAction(host, PendingFlushAction.FlushNow);

            Assert.Equal(new[] { "start:message-batch-threshold" }, host.Log);
        }

        [Fact]
        public void ApplyAction_schedule_timer_arms_timer()
        {
            var host = new FakeHost();

            OfflineReplayFlushDispatch.ApplyAction(host, PendingFlushAction.ScheduleTimer);

            Assert.Equal(new[] { "schedule-timer" }, host.Log);
        }

        [Fact]
        public async Task Idle_timer_skips_when_claim_fails()
        {
            var host = new FakeHost { ClaimFlush = false };

            await OfflineReplayFlushDispatch.OnIdleTimerAsync(host);

            Assert.Equal(new[] { "try-claim" }, host.Log);
        }

        [Fact]
        public async Task Flush_empty_drain_releases_lock()
        {
            var host = new FakeHost { Drain = null };

            await OfflineReplayFlushDispatch.FlushAsync(host, "r");

            Assert.Equal(
                new[] { "enter-lock", "drain", "complete-flush", "release-lock" },
                host.Log);
        }

        [Fact]
        public async Task Flush_persists_removes_pending_and_schedules_catalog()
        {
            var host = new FakeHost
            {
                Drain = new PendingMessageDrain(
                    new Dictionary<string, List<ChatMessage>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["c@s.whatsapp.net"] = new List<ChatMessage>
                        {
                            new ChatMessage { Id = "o1", IsFromMe = true },
                            new ChatMessage { Id = "i1", IsFromMe = false }
                        }
                    },
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "c@s.whatsapp.net" })
            };

            await OfflineReplayFlushDispatch.FlushAsync(host, "threshold");

            Assert.Contains("persist:c@s.whatsapp.net:2", host.Log);
            Assert.Contains("remove-out:o1", host.Log);
            Assert.Contains("remove-in:i1", host.Log);
            Assert.Contains("log-flushed:2:1:1:threshold", host.Log);
            Assert.Contains("catalog-persist", host.Log);
            Assert.Contains("release-lock", host.Log);
        }

        [Fact]
        public async Task Flush_failure_restores_drain()
        {
            var host = new FakeHost
            {
                Drain = new PendingMessageDrain(
                    new Dictionary<string, List<ChatMessage>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["c@s.whatsapp.net"] = new List<ChatMessage>
                        {
                            new ChatMessage { Id = "x", IsFromMe = true }
                        }
                    },
                    new HashSet<string>()),
                FailPersist = true,
                CompleteFlushReturnsTrue = true
            };

            await OfflineReplayFlushDispatch.FlushAsync(host, "fail");

            Assert.Contains("restore", host.Log);
            Assert.Contains(host.Log, line => line.StartsWith("deferred:"));
            Assert.Contains("schedule-timer", host.Log);
        }

        private sealed class FakeHost : IOfflineReplayFlushHost
        {
            public List<string> Log { get; } = new List<string>();

            public PendingMessageDrain Drain { get; set; }

            public bool ClaimFlush { get; set; } = true;

            public bool FailPersist { get; set; }

            public bool CompleteFlushReturnsTrue { get; set; }

            public Task EnterFlushLockAsync()
            {
                Log.Add("enter-lock");
                return Task.CompletedTask;
            }

            public void ReleaseFlushLock() => Log.Add("release-lock");

            public PendingMessageDrain DrainPending(DateTime utcNow)
            {
                Log.Add("drain");
                return Drain;
            }

            public void ClearFlushTimer() => Log.Add("clear-timer");

            public List<ChatMessage> SelectBatch(IEnumerable<ChatMessage> queued) =>
                queued?.Where(m => m != null).ToList() ?? new List<ChatMessage>();

            public Task PersistLiveMessagesAsync(string chatJid, IList<ChatMessage> messages)
            {
                if (FailPersist)
                {
                    throw new InvalidOperationException("persist");
                }

                Log.Add("persist:" + chatJid + ":" + messages.Count);
                return Task.CompletedTask;
            }

            public Task RemovePendingOutgoingAsync(IEnumerable<string> ids)
            {
                Log.Add("remove-out:" + string.Join(",", ids));
                return Task.CompletedTask;
            }

            public Task RemovePendingIncomingAsync(IEnumerable<string> ids)
            {
                Log.Add("remove-in:" + string.Join(",", ids));
                return Task.CompletedTask;
            }

            public void RestoreDrain(PendingMessageDrain drain) => Log.Add("restore");

            public void LogFlushed(
                int saved,
                int chatCount,
                int dirtyChatCount,
                string reason) =>
                Log.Add(
                    "log-flushed:" + saved + ":" + chatCount + ":" + dirtyChatCount + ":" +
                    reason);

            public void ScheduleCatalogPersist() => Log.Add("catalog-persist");

            public void RecordFlushDeferred(Exception ex, string reason, int chatCount) =>
                Log.Add("deferred:" + reason + ":" + chatCount);

            public bool CompleteFlush()
            {
                Log.Add("complete-flush");
                return CompleteFlushReturnsTrue;
            }

            public void ScheduleFlushTimer() => Log.Add("schedule-timer");

            public bool TryClaimFlush()
            {
                Log.Add("try-claim");
                return ClaimFlush;
            }

            public void LogNonFatalIdleFlushFailure(Exception ex) =>
                Log.Add("idle-fail");

            public void StartFlush(string reason) => Log.Add("start:" + reason);
        }
    }
}