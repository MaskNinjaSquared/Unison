using System;
using System.Collections.Generic;
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
        public void A_blank_address_is_not_recorded()
        {
            int applies = 0;
            var tips = new OfflineReplayTips(new FakeJids(), new FakeHost(), (batch, _) => { applies++; return Task.CompletedTask; }, NoTimer);

            tips.Record(Request(" ", countUnread: true));
            tips.Record(null);
            tips.FlushAsync("test").GetAwaiter().GetResult();

            Assert.Equal(0, applies);
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

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;
        }
    }
}
