using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PostMessageEnrichmentDispatchTests
    {
        [Fact]
        public void Classify_prefer_already_awaiting_heavy()
        {
            PostMessageEnrichmentTryDecision d = PostMessageEnrichmentGates.ClassifyTry(
                enrichmentAwaitingHeavyHistory: true,
                historyCatchUpActive: true,
                fullHistoryInFlight: true,
                listPaintPending: true);

            Assert.Equal(PostMessageEnrichmentTryKind.AlreadyAwaitingHeavyHistory, d.Kind);
        }

        [Fact]
        public void Classify_catch_up_before_full_history()
        {
            PostMessageEnrichmentTryDecision d = PostMessageEnrichmentGates.ClassifyTry(
                enrichmentAwaitingHeavyHistory: false,
                historyCatchUpActive: true,
                fullHistoryInFlight: true,
                listPaintPending: false);

            Assert.Equal(PostMessageEnrichmentTryKind.DeferCatchUp, d.Kind);
            Assert.Equal("history-catch-up", d.CatchUpDeferReason);
        }

        [Fact]
        public void Classify_list_paint_when_otherwise_clear()
        {
            PostMessageEnrichmentTryDecision d = PostMessageEnrichmentGates.ClassifyTry(
                enrichmentAwaitingHeavyHistory: false,
                historyCatchUpActive: false,
                fullHistoryInFlight: false,
                listPaintPending: true);

            Assert.Equal(PostMessageEnrichmentTryKind.DeferListPaint, d.Kind);
        }

        [Fact]
        public void TrySchedule_proceed_begins_schedule()
        {
            var host = new FakeHost();

            PostMessageEnrichmentDispatch.TrySchedule(host, "post-replay:3");

            Assert.Equal(new[] { "begin:post-replay:3" }, host.Log);
        }

        [Fact]
        public void TrySchedule_catch_up_marks_heavy_and_writes()
        {
            var host = new FakeHost { CatchUpActive = true };

            PostMessageEnrichmentDispatch.TrySchedule(host, "x");

            Assert.Equal(
                new[]
                {
                    "mark-heavy",
                    "catch-up:x|history-catch-up"
                },
                host.Log);
        }

        [Fact]
        public async Task Run_parallel_triggers_background_resolution()
        {
            var host = new FakeHost { Serialize = false };

            await PostMessageEnrichmentDispatch.RunAsync(
                host,
                "r1",
                CancellationToken.None);

            Assert.Equal(
                new[]
                {
                    "quiet:post-message-enrich",
                    "resolve-names",
                    "quiet:post-message-enrich-contacts",
                    "refresh-contacts",
                    "trigger-bg",
                    "complete:r1:False"
                },
                host.Log);
        }

        [Fact]
        public async Task Run_serial_uses_serialized_path()
        {
            var host = new FakeHost { Serialize = true };

            await PostMessageEnrichmentDispatch.RunAsync(
                host,
                "r2",
                CancellationToken.None);

            Assert.Contains("quiet:post-message-enrich-avatars", host.Log);
            Assert.Contains("pictures", host.Log);
            Assert.Contains("quiet:post-message-enrich-groups", host.Log);
            Assert.Contains("group-listing", host.Log);
            Assert.DoesNotContain("trigger-bg", host.Log);
            Assert.Contains("complete:r2:True", host.Log);
        }

        [Fact]
        public async Task Serialized_defers_avatars_when_gate_says_so()
        {
            var host = new FakeHost { DeferAvatars = true, AvatarDeferReason = "replay" };

            await PostMessageEnrichmentDispatch.RunSerializedAsync(
                host,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.Equal(
                new[]
                {
                    "quiet:post-message-enrich-avatars",
                    "defer-avatars:replay",
                    "quiet:post-message-enrich-groups",
                    "clear-status",
                    "group-listing"
                },
                host.Log);
        }

        [Fact]
        public async Task Run_quiet_fail_clears_status()
        {
            var host = new FakeHost { QuietOk = false };

            await PostMessageEnrichmentDispatch.RunAsync(
                host,
                "r3",
                CancellationToken.None);

            Assert.Equal(
                new[] { "quiet:post-message-enrich", "clear-status" },
                host.Log);
        }

        private sealed class FakeHost : IPostMessageEnrichmentHost
        {
            public List<string> Log { get; } = new List<string>();

            public bool CatchUpActive { get; set; }

            public bool FullHistoryInFlight { get; set; }

            public bool ListPaintPending { get; set; }

            public bool AwaitingHeavy { get; set; }

            public bool QuietOk { get; set; } = true;

            public bool Serialize { get; set; }

            public bool DeferAvatars { get; set; }

            public string AvatarDeferReason { get; set; }

            public bool EnrichmentAwaitingHeavyHistory => AwaitingHeavy;

            public bool IsHistoryCatchUpActive => CatchUpActive;

            public bool IsFullHistoryInFlight => FullHistoryInFlight;

            public bool IsListPaintPending => ListPaintPending;

            public bool IsConnected => true;

            public bool IsWindowVisible => true;

            public bool SerializePostSyncEnrichment => Serialize;

            public TimeSpan EnrichmentQuietWait => TimeSpan.FromSeconds(1);

            public void LogDeferUntilHeavyFinishes(string reason) =>
                Log.Add("defer-heavy:" + reason);

            public void MarkAwaitingHeavyHistory() => Log.Add("mark-heavy");

            public void WriteCatchUpDeferred(string reason, string catchUpDeferReason) =>
                Log.Add("catch-up:" + reason + "|" + catchUpDeferReason);

            public void LogArmAfterFullHistory(string reason) =>
                Log.Add("arm-full:" + reason);

            public void MarkAwaitingListPaint() => Log.Add("mark-paint");

            public void WriteListPaintDeferred(string reason) =>
                Log.Add("paint:" + reason);

            public void BeginEnrichmentSchedule(string reason) =>
                Log.Add("begin:" + reason);

            public Task<bool> WaitForStartupQuietAsync(
                TimeSpan timeout,
                string phase,
                CancellationToken token)
            {
                Log.Add("quiet:" + phase);
                return Task.FromResult(QuietOk);
            }

            public void ClearSyncStatus() => Log.Add("clear-status");

            public Task ResolveMissingNamesAsync(CancellationToken token)
            {
                Log.Add("resolve-names");
                return Task.CompletedTask;
            }

            public Task RefreshContactNamesAsync(CancellationToken token)
            {
                Log.Add("refresh-contacts");
                return Task.CompletedTask;
            }

            public void TriggerBackgroundResolution() => Log.Add("trigger-bg");

            public bool TryDeferAvatarFetch(out string reason)
            {
                reason = AvatarDeferReason;
                return DeferAvatars;
            }

            public void ScheduleDeferredAvatars(string reason) =>
                Log.Add("defer-avatars:" + reason);

            public Task RetrieveContactPicturesAsync(CancellationToken token)
            {
                Log.Add("pictures");
                return Task.CompletedTask;
            }

            public void NotifyGroupListingDue() => Log.Add("group-listing");

            public void WriteComplete(string reason, bool serialized) =>
                Log.Add("complete:" + reason + ":" + serialized);

            public void RecordFailed(Exception ex, string reason) =>
                Log.Add("failed:" + reason);
        }
    }
}