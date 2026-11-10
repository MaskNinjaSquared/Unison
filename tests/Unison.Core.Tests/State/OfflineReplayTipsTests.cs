using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class OfflineReplayTipsTests
    {
        private static readonly TimeSpan NoTimer = TimeSpan.FromHours(1);

        [Fact]
        public void A_tip_is_filed_under_the_canonical_chat()
        {
            var jids = new FakeJids();
            jids.Canonical["111@lid"] = "111@s.whatsapp.net";
            var host = new FakeHost();
            var applied = new List<IReadOnlyList<OfflineReplayChatSummary>>();
            var tips = new OfflineReplayTips(jids, host, (batch, _) => { applied.Add(batch); return Task.CompletedTask; }, NoTimer);

            tips.Record(Request("111@lid", countUnread: true));
            tips.FlushAsync("test").GetAwaiter().GetResult();

            Assert.Equal("111@s.whatsapp.net", applied[0][0].Jid);
            Assert.Equal(1, applied[0][0].UnreadDelta);
        }

        [Fact]
        public void An_open_chat_on_a_visible_window_does_not_add_unread_and_a_duplicate_never_does()
        {
            var host = new FakeHost { ActiveChat = "111@s.whatsapp.net", WindowVisible = true };
            IReadOnlyList<OfflineReplayChatSummary> applied = null;
            var tips = new OfflineReplayTips(new FakeJids(), host, (batch, _) => { applied = batch; return Task.CompletedTask; }, NoTimer);

            tips.Record(Request("111@s.whatsapp.net", countUnread: true));
            tips.Record(Request("222@s.whatsapp.net", countUnread: false));
            tips.FlushAsync("test").GetAwaiter().GetResult();

            Assert.All(applied, summary => Assert.Equal(0, summary.UnreadDelta));
        }

        [Fact]
        public void A_hidden_window_counts_the_open_chat_as_unread()
        {
            var host = new FakeHost { ActiveChat = "111@s.whatsapp.net", WindowVisible = false };
            IReadOnlyList<OfflineReplayChatSummary> applied = null;
            var tips = new OfflineReplayTips(new FakeJids(), host, (batch, _) => { applied = batch; return Task.CompletedTask; }, NoTimer);

            tips.Record(Request("111@s.whatsapp.net", countUnread: true));
            tips.FlushAsync("test").GetAwaiter().GetResult();

            Assert.Equal(1, applied[0].UnreadDelta);
        }

        [Fact]
        public void A_flush_reports_progress_and_persists_and_an_empty_book_does_nothing()
        {
            var host = new FakeHost();
            int applies = 0;
            var tips = new OfflineReplayTips(new FakeJids(), host, (batch, _) => { applies++; return Task.CompletedTask; }, NoTimer);

            tips.FlushAsync("empty").GetAwaiter().GetResult();
            Assert.Equal(0, applies);
            Assert.Equal(0, host.Persists);

            tips.Record(Request("111@s.whatsapp.net", countUnread: false));
            tips.Record(Request("222@s.whatsapp.net", countUnread: false));
            tips.FlushAsync("offline-complete:2").GetAwaiter().GetResult();

            Assert.Equal(1, applies);
            Assert.Equal(1, host.Persists);
            Assert.Equal("offline-summary:offline-complete:2:chats=2", host.Progress[0]);
        }

        [Fact]
        public void IsBusy_while_a_tip_is_filed_and_clears_after_flush()
        {
            var tips = new OfflineReplayTips(
                new FakeJids(),
                new FakeHost(),
                (batch, _) => Task.CompletedTask,
                NoTimer);

            Assert.False(tips.IsBusy);
            tips.Record(Request("111@s.whatsapp.net", countUnread: false));
            Assert.True(tips.IsBusy);
            tips.FlushAsync("test").GetAwaiter().GetResult();
            Assert.False(tips.IsBusy);
        }

        [Fact]
        public void IsBusy_stays_true_during_post_flush_quiet()
        {
            var tips = new OfflineReplayTips(
                new FakeJids(),
                new FakeHost(),
                (batch, _) => Task.CompletedTask,
                NoTimer,
                postFlushQuiet: () => TimeSpan.FromMilliseconds(200));

            tips.Record(Request("111@s.whatsapp.net", countUnread: false));
            tips.FlushAsync("test").GetAwaiter().GetResult();
            Assert.True(tips.IsBusy);
        }

        [Fact]
        public async Task IsBusy_while_apply_is_in_flight()
        {
            var applyStarted = new TaskCompletionSource<bool>();
            var releaseApply = new TaskCompletionSource<bool>();
            var tips = new OfflineReplayTips(
                new FakeJids(),
                new FakeHost(),
                async (batch, _) =>
                {
                    applyStarted.TrySetResult(true);
                    await releaseApply.Task.ConfigureAwait(true);
                },
                NoTimer);

            tips.Record(Request("111@s.whatsapp.net", countUnread: false));
            Task flush = tips.FlushAsync("in-flight");
            await applyStarted.Task.ConfigureAwait(true);
            Assert.True(tips.IsBusy);
            releaseApply.TrySetResult(true);
            await flush.ConfigureAwait(true);
            Assert.False(tips.IsBusy);
        }

        [Fact]
        public void A_failed_flush_puts_the_tips_back_and_rethrows()
        {
            var host = new FakeHost();
            bool fail = true;
            IReadOnlyList<OfflineReplayChatSummary> applied = null;
            var tips = new OfflineReplayTips(
                new FakeJids(),
                host,
                (batch, _) =>
                {
                    if (fail)
                    {
                        throw new InvalidOperationException("ui");
                    }

                    applied = batch;
                    return Task.CompletedTask;
                },
                NoTimer);

            tips.Record(Request("111@s.whatsapp.net", countUnread: true));
            Assert.Throws<InvalidOperationException>(() => tips.FlushAsync("first").GetAwaiter().GetResult());
            Assert.Equal(0, host.Persists);

            fail = false;
            tips.FlushAsync("second").GetAwaiter().GetResult();
            Assert.Equal(1, applied[0].UnreadDelta);
        }

        [Fact]
        public async Task The_first_tip_arms_the_timer_and_a_timer_failure_is_reported()
        {
            var host = new FakeHost();
            var reported = new TaskCompletionSource<string>();
            host.OnFailure = reason => reported.TrySetResult(reason);
            var tips = new OfflineReplayTips(
                new FakeJids(),
                host,
                (batch, _) => { throw new InvalidOperationException("ui"); },
                TimeSpan.FromMilliseconds(10));

            tips.Record(Request("111@s.whatsapp.net", countUnread: false));

            Task finished = await Task.WhenAny(reported.Task, Task.Delay(5000));
            Assert.Same(reported.Task, finished);
            Assert.Equal(OfflineReplayTips.ProgressiveReason, reported.Task.Result);
        }

        [Fact]
        public async Task After_the_first_progressive_flush_tips_wait_the_steady_window_until_the_replay_ends()
        {
            var host = new FakeHost();
            var applied = new List<string>();
            var firstApplied = new TaskCompletionSource<bool>();
            var resetApplied = new TaskCompletionSource<bool>();
            var tips = new OfflineReplayTips(
                new FakeJids(),
                host,
                (batch, reason) =>
                {
                    lock (applied)
                    {
                        applied.Add(reason);
                        if (applied.Count == 1) firstApplied.TrySetResult(true);
                        if (applied.Count == 3) resetApplied.TrySetResult(true);
                    }

                    return Task.CompletedTask;
                },
                TimeSpan.FromMilliseconds(10),
                NoTimer);

            tips.Record(Request("111@s.whatsapp.net", countUnread: false));
            Assert.Same(firstApplied.Task, await Task.WhenAny(firstApplied.Task, Task.Delay(5000)));

            tips.Record(Request("222@s.whatsapp.net", countUnread: false));
            await Task.Delay(200);
            lock (applied)
            {
                Assert.Single(applied);
            }

            await tips.FlushAsync("offline-complete:1");

            tips.Record(Request("333@s.whatsapp.net", countUnread: false));
            Assert.Same(resetApplied.Task, await Task.WhenAny(resetApplied.Task, Task.Delay(5000)));
            Assert.Equal(
                new[] { OfflineReplayTips.ProgressiveReason, "offline-complete:1", OfflineReplayTips.ProgressiveReason },
                applied);
        }

        [Fact]
        public void A_blank_address_is_not_recorded()
        {
            int applies = 0;
            var tips = new OfflineReplayTips(new FakeJids(), new FakeHost(), (batch, _) => { applies++; return Task.CompletedTask; }, NoTimer);

            tips.Record(Request(" ", countUnread: true));
            tips.Record(null);
            tips.FlushAsync("test").GetAwaiter().GetResult();

            Assert.Equal(0, applies);
        }

        [Fact]
        public async Task PreferFrugal_defers_progressive_paint_until_settle_flush()
        {
            int applies = 0;
            IReadOnlyList<OfflineReplayChatSummary> last = null;
            var tips = new OfflineReplayTips(
                new FakeJids(),
                new FakeHost(),
                (batch, _) =>
                {
                    applies++;
                    last = batch;
                    return Task.CompletedTask;
                },
                TimeSpan.FromMilliseconds(10),
                deferTipPaintUntilSettle: () => true);

            tips.Record(Request("111@s.whatsapp.net", countUnread: true));
            tips.Record(Request("222@s.whatsapp.net", countUnread: false));
            await Task.Delay(80);
            Assert.Equal(0, applies);
            Assert.True(tips.IsBusy);

            await tips.FlushAsync("offline-complete:2");
            Assert.Equal(1, applies);
            Assert.Equal(2, last.Count);
            Assert.False(tips.IsBusy);
        }

        [Fact]
        public async Task PreferFrugal_progressive_FlushAsync_does_not_take_the_book()
        {
            int applies = 0;
            var tips = new OfflineReplayTips(
                new FakeJids(),
                new FakeHost(),
                (batch, _) =>
                {
                    applies++;
                    return Task.CompletedTask;
                },
                NoTimer,
                deferTipPaintUntilSettle: () => true);

            tips.Record(Request("111@s.whatsapp.net", countUnread: false));
            await tips.FlushAsync(OfflineReplayTips.ProgressiveReason);
            Assert.Equal(0, applies);
            Assert.True(tips.IsBusy);

            await tips.FlushAsync("offline-complete:1");
            Assert.Equal(1, applies);
        }

        [Fact]
        public async Task PreferFrugal_late_record_with_no_settle_is_painted_by_the_backstop()
        {
            var reasons = new List<string>();
            var tips = new OfflineReplayTips(
                new FakeJids(),
                new FakeHost(),
                (batch, reason) => { lock (reasons) { reasons.Add(reason); } return Task.CompletedTask; },
                NoTimer,
                deferTipPaintUntilSettle: () => true,
                deferredBackstop: TimeSpan.FromMilliseconds(60));

            tips.Record(Request("111@s.whatsapp.net", countUnread: true));
            Assert.True(tips.IsBusy);

            await WaitUntilAsync(() => !tips.IsBusy, TimeSpan.FromSeconds(3));
            Assert.Equal(new[] { OfflineReplayTips.BackstopReason }, reasons);
        }

        [Fact]
        public async Task PreferFrugal_settle_flush_before_backstop_paints_once()
        {
            int applies = 0;
            var tips = new OfflineReplayTips(
                new FakeJids(),
                new FakeHost(),
                (batch, _) => { Interlocked.Increment(ref applies); return Task.CompletedTask; },
                NoTimer,
                deferTipPaintUntilSettle: () => true,
                deferredBackstop: TimeSpan.FromMilliseconds(120));

            tips.Record(Request("111@s.whatsapp.net", countUnread: true));
            await tips.FlushAsync("offline-complete:1");
            await Task.Delay(250);

            Assert.Equal(1, applies);
            Assert.False(tips.IsBusy);
        }

        [Fact]
        public async Task PreferFrugal_backstop_keeps_first_deadline_under_record_flood()
        {
            int applies = 0;
            var firstApply = new TaskCompletionSource<bool>();
            var tips = new OfflineReplayTips(
                new FakeJids(),
                new FakeHost(),
                (batch, _) =>
                {
                    if (Interlocked.Increment(ref applies) == 1)
                    {
                        firstApply.TrySetResult(true);
                    }

                    return Task.CompletedTask;
                },
                NoTimer,
                deferTipPaintUntilSettle: () => true,
                deferredBackstop: TimeSpan.FromMilliseconds(120));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            tips.Record(Request("111@s.whatsapp.net", countUnread: false));
            Task flood = Task.Run(async () =>
            {
                for (int i = 0; i < 8; i++)
                {
                    tips.Record(Request("111@s.whatsapp.net", countUnread: false));
                    await Task.Delay(40);
                }
            });

            Assert.Same(firstApply.Task, await Task.WhenAny(firstApply.Task, Task.Delay(3000)));
            // First deadline (~120ms) must win: a pushable debounce would still be arming past ~400ms.
            Assert.True(watch.ElapsedMilliseconds < 280, "elapsedMs=" + watch.ElapsedMilliseconds);
            Assert.True(Volatile.Read(ref applies) >= 1);
            await flood;
        }

        [Fact]
        public async Task PreferFrugal_post_settle_records_paint_progressively_without_backstop()
        {
            var reasons = new List<string>();
            bool drainActive = true;
            var tips = new OfflineReplayTips(
                new FakeJids(),
                new FakeHost(),
                (batch, reason) => { lock (reasons) { reasons.Add(reason); } return Task.CompletedTask; },
                TimeSpan.FromMilliseconds(20),
                deferTipPaintUntilSettle: () => drainActive,
                deferredBackstop: TimeSpan.FromMilliseconds(500));

            tips.Record(Request("111@s.whatsapp.net", countUnread: false));
            await tips.FlushAsync("offline-complete:1");
            drainActive = false;

            tips.Record(Request("222@s.whatsapp.net", countUnread: false));
            await WaitUntilAsync(
                () =>
                {
                    lock (reasons)
                    {
                        return reasons.Count >= 2;
                    }
                },
                TimeSpan.FromSeconds(3));

            lock (reasons)
            {
                Assert.Equal(
                    new[] { "offline-complete:1", OfflineReplayTips.ProgressiveReason },
                    reasons);
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed > timeout)
                {
                    throw new TimeoutException("condition not met");
                }

                await Task.Delay(10);
            }
        }

        private static OfflineReplayTipRequest Request(string jid, bool countUnread) =>
            new OfflineReplayTipRequest
            {
                Jid = jid,
                Preview = "hi",
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CountUnread = countUnread
            };

        private sealed class FakeHost : IOfflineReplayHost
        {
            public string ActiveChat { get; set; }
            public bool WindowVisible { get; set; }
            public int Persists { get; private set; }
            public List<string> Progress { get; } = new List<string>();
            public Action<string> OnFailure { get; set; }

            public bool IsWindowVisible => WindowVisible;

            public bool IsActiveChat(string canonicalJid) =>
                string.Equals(ActiveChat, canonicalJid, StringComparison.OrdinalIgnoreCase);

            public void NoteCatchUpProgress(string reason) => Progress.Add(reason);

            public void CompleteFirstOfflineRelease(string reason)
            {
            }

            public void SchedulePersist() => Persists++;


            public void ReportFlushFailure(string reason, Exception error) => OnFailure?.Invoke(reason);
        }

        private sealed class FakeJids : IJidResolver
        {
            public Dictionary<string, string> Canonical { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Profile Self => null;

            public string GetCanonicalJid(string jid)
            {
                string key = JidHelper.Normalize(jid);
                string value;
                return key != null && Canonical.TryGetValue(key, out value) ? value : key;
            }

            public string PeekCanonicalJid(string jid) => GetCanonicalJid(jid);

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;
        }
    }
}
