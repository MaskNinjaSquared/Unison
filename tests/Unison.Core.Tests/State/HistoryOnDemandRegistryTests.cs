// =============================================================================
// Tests for HistoryOnDemandRegistry.
//
// What history has been asked of the phone and is still owed. A request that is
// forgotten too early lets the same page be asked twice; one that is never
// forgotten blocks the chat (or the whole catch-up) until the next connect, and
// nothing on screen says why.
// =============================================================================
using System;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class HistoryOnDemandRegistryTests
    {
        private const string Chat = "5511999990000@s.whatsapp.net";
        private const string OtherChat = "5511888880000@s.whatsapp.net";
        private static readonly DateTime T0 = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        private static HistoryOnDemandRequest StartChat(
            HistoryOnDemandRegistry registry,
            string chat = Chat,
            string requestId = "R1",
            string marker = "M1")
        {
            Assert.Equal(HistoryOnDemandChatGate.Started, registry.TryBeginChat(chat, marker, T0, out _));
            return registry.RegisterChat(chat, requestId, marker, baselineMessageCount: 10, triggerReason: "scroll", nowUtc: T0);
        }

        private static HistoryOnDemandRequest StartFull(
            HistoryOnDemandRegistry registry,
            string stanzaId = "F1",
            bool isRepair = true)
        {
            Assert.True(registry.TryClaimFull());
            return registry.RegisterFull(stanzaId, isRepair, baselineMessageCount: 100, reason: "offline-stale", nowUtc: T0);
        }

        // --- Per-chat requests -------------------------------------------------

        [Fact]
        public void A_registered_chat_request_is_pending_and_findable()
        {
            var registry = new HistoryOnDemandRegistry();

            var request = StartChat(registry);

            Assert.True(registry.IsChatPending(Chat));
            Assert.Equal(1, registry.ChatsInFlight);
            Assert.Same(request, registry.Find("R1"));
            Assert.Equal(HistoryOnDemandKind.Chat, request.Kind);
            Assert.Equal("HistorySyncOnDemand", request.TypeName);
            Assert.Equal(10, request.BaselineMessageCount);
            Assert.Equal("scroll", request.TriggerReason);
        }

        [Fact]
        public void A_chat_already_in_flight_is_not_asked_again()
        {
            var registry = new HistoryOnDemandRegistry();
            StartChat(registry);

            Assert.Equal(HistoryOnDemandChatGate.AlreadyInFlight, registry.TryBeginChat(Chat, "M2", T0, out _));
        }

        [Fact]
        public void The_same_oldest_message_is_not_asked_about_twice()
        {
            var registry = new HistoryOnDemandRegistry();
            var request = StartChat(registry);
            registry.CompleteChats(new[] { Chat });

            // Completing a chat forgets the request but not the marker: the phone already
            // answered for that anchor.
            Assert.Equal(HistoryOnDemandChatGate.SameMarker, registry.TryBeginChat(Chat, "M1", T0, out _));
            Assert.Equal(HistoryOnDemandChatGate.Started, registry.TryBeginChat(Chat, "M2", T0, out _));
            Assert.Null(registry.Find(request.RequestId));
        }

        [Fact]
        public void A_failed_send_frees_the_chat_but_keeps_its_marker()
        {
            // Recorded, not endorsed: the marker is written before the send and stays after
            // a failure, so a retry from the same oldest message is refused until the next
            // connect clears it.
            var registry = new HistoryOnDemandRegistry();
            StartChat(registry);

            registry.FailChat(Chat, "R1");

            Assert.False(registry.IsChatPending(Chat));
            Assert.Null(registry.Find("R1"));
            Assert.Equal(HistoryOnDemandChatGate.SameMarker, registry.TryBeginChat(Chat, "M1", T0, out _));
        }

        [Fact]
        public void A_failure_before_registering_only_releases_the_in_flight_slot()
        {
            var registry = new HistoryOnDemandRegistry();
            StartChat(registry, requestId: "OLD", marker: "M0");
            registry.CompleteChats(new[] { Chat });
            Assert.Equal(HistoryOnDemandChatGate.Started, registry.TryBeginChat(Chat, "M1", T0, out _));

            registry.FailChat(Chat, requestId: null);

            Assert.False(registry.IsChatPending(Chat));
            Assert.Equal(HistoryOnDemandChatGate.Started, registry.TryBeginChat(Chat, "M1", T0, out _));
        }

        [Fact]
        public void A_rejected_ack_backs_the_chat_off_for_three_minutes()
        {
            var registry = new HistoryOnDemandRegistry();
            StartChat(registry);

            var acked = registry.Ack("R1", "500", T0);

            Assert.NotNull(acked);
            Assert.False(registry.IsChatPending(Chat));
            Assert.Equal(HistoryOnDemandChatGate.BackedOff, registry.TryBeginChat(Chat, "M9", T0.AddMinutes(2), out DateTime until));
            Assert.Equal(T0.AddMinutes(3), until);
            Assert.Equal(HistoryOnDemandChatGate.Started, registry.TryBeginChat(Chat, "M9", T0.AddMinutes(3).AddSeconds(1), out _));
        }

        [Fact]
        public void A_479_rejection_backs_off_for_fifteen_minutes()
        {
            var registry = new HistoryOnDemandRegistry();
            StartChat(registry);

            registry.Ack("R1", "479", T0);

            Assert.Equal(HistoryOnDemandChatGate.BackedOff, registry.TryBeginChat(Chat, "M9", T0.AddMinutes(14), out _));
        }

        [Fact]
        public void An_accepted_ack_stamps_the_request_and_keeps_it_pending()
        {
            var registry = new HistoryOnDemandRegistry();
            var request = StartChat(registry);

            registry.Ack("R1", null, T0.AddSeconds(2));

            Assert.True(request.AckAccepted);
            Assert.Equal(T0.AddSeconds(2), request.AckAcceptedUtc);
            Assert.True(registry.IsChatPending(Chat));
        }

        [Fact]
        public void An_ack_for_an_unknown_id_changes_nothing()
        {
            var registry = new HistoryOnDemandRegistry();
            StartChat(registry);

            Assert.Null(registry.Ack("nope", "479", T0));
            Assert.True(registry.IsChatPending(Chat));
        }

        [Fact]
        public void Completing_chats_clears_their_backoff_and_requests()
        {
            var registry = new HistoryOnDemandRegistry();
            StartChat(registry);
            registry.Ack("R1", "479", T0);
            StartChat(registry, OtherChat, "R2", "M2");

            registry.CompleteChats(new[] { Chat, OtherChat, null!, " " });

            Assert.Equal(0, registry.ChatsInFlight);
            Assert.Null(registry.Find("R2"));
            Assert.Equal(HistoryOnDemandChatGate.Started, registry.TryBeginChat(Chat, "M9", T0, out _));
        }

        [Fact]
        public void Forgetting_a_deleted_chat_drops_marker_and_backoff_but_not_an_in_flight_request()
        {
            var registry = new HistoryOnDemandRegistry();
            var request = StartChat(registry);
            registry.ForgetChat(Chat);

            Assert.True(registry.IsChatPending(Chat));
            Assert.Same(request, registry.Find("R1"));
            registry.FailChat(Chat, "R1");
            Assert.Equal(HistoryOnDemandChatGate.Started, registry.TryBeginChat(Chat, "M1", T0, out _));
        }

        // --- Timeout and expiry ------------------------------------------------

        [Fact]
        public void A_timeout_is_armed_once_per_request()
        {
            var registry = new HistoryOnDemandRegistry();
            var request = StartChat(registry);

            Assert.True(registry.TryArmTimeout(request));
            Assert.False(registry.TryArmTimeout(request));
        }

        [Fact]
        public void Expiring_a_chat_request_returns_a_detached_copy_and_frees_the_chat()
        {
            var registry = new HistoryOnDemandRegistry();
            var request = StartChat(registry);

            var expired = registry.TryExpire(request);

            Assert.NotNull(expired);
            Assert.NotSame(request, expired);
            Assert.Equal("R1", expired!.RequestId);
            Assert.False(registry.IsChatPending(Chat));
            Assert.Null(registry.TryExpire(request));
        }

        [Fact]
        public void A_superseded_request_cannot_be_expired_or_cleared()
        {
            // The watchdog and the timeout hold the object they started with; a newer request
            // under the same id must not be cleared on the old one's behalf.
            var registry = new HistoryOnDemandRegistry();
            var stale = StartChat(registry);
            registry.FailChat(Chat, "R1");
            StartChat(registry, requestId: "R1", marker: "M2");

            Assert.Null(registry.TryExpire(stale));
            Assert.False(registry.TryClear(stale));
            Assert.Null(registry.TrySnapshot(stale));
            Assert.True(registry.IsChatPending(Chat));
        }

        // --- Full history ------------------------------------------------------

        [Fact]
        public void Full_history_is_claimed_once_per_session()
        {
            var registry = new HistoryOnDemandRegistry();

            Assert.True(registry.TryClaimFull());
            Assert.False(registry.TryClaimFull());
            Assert.True(registry.IsFullClaimed);
            Assert.False(registry.IsFullInFlight);
        }

        [Fact]
        public void A_registered_full_request_is_in_flight_with_progress_stamped_at_send()
        {
            var registry = new HistoryOnDemandRegistry();

            var request = StartFull(registry);

            Assert.True(registry.IsFullInFlight);
            Assert.Equal("F1", registry.FullRequestId);
            Assert.Equal(HistoryOnDemandKind.FullHistory, request.Kind);
            Assert.Equal("FullHistorySyncOnDemand", request.TypeName);
            Assert.Equal(T0, request.LastProgressUtc);
            Assert.Equal("request-sent", request.LastProgressReason);
            Assert.Equal("offline-stale", request.Marker);
        }

        [Fact]
        public void A_failed_full_send_releases_the_claim()
        {
            var registry = new HistoryOnDemandRegistry();
            StartFull(registry);

            registry.FailFull(isRepair: true);

            Assert.False(registry.IsFullClaimed);
            Assert.Null(registry.FullRequestId);
            Assert.Null(registry.Find("F1"));
            Assert.True(registry.TryClaimFull());
        }

        [Fact]
        public void Expiring_the_full_request_releases_the_session_claim()
        {
            var registry = new HistoryOnDemandRegistry();
            var request = StartFull(registry);

            Assert.NotNull(registry.TryExpire(request));

            Assert.False(registry.IsFullClaimed);
            Assert.Null(registry.FullRequestId);
        }

        [Fact]
        public void Clearing_full_history_reports_whether_a_tracked_request_was_dropped()
        {
            var registry = new HistoryOnDemandRegistry();
            StartFull(registry);

            Assert.Equal(HistoryOnDemandFullClear.Tracked, registry.ClearFull(out string id, out _));
            Assert.Equal("F1", id);
            Assert.False(registry.IsFullClaimed);
            Assert.Equal(HistoryOnDemandFullClear.Nothing, registry.ClearFull(out _, out _));
        }

        [Fact]
        public void Clearing_full_history_drops_a_claim_with_nothing_tracked_behind_it()
        {
            var registry = new HistoryOnDemandRegistry();
            Assert.True(registry.TryClaimFull());

            Assert.Equal(HistoryOnDemandFullClear.Stale, registry.ClearFull(out _, out _));
            Assert.True(registry.TryClaimFull());
        }

        [Fact]
        public void A_forced_release_drops_both_tracked_ids()
        {
            var registry = new HistoryOnDemandRegistry();
            StartFull(registry, "F1", isRepair: true);

            registry.ForceReleaseFull();

            Assert.Null(registry.Find("F1"));
            Assert.False(registry.IsFullClaimed);
            Assert.True(registry.TryClaimFull());
        }

        [Fact]
        public void An_untracked_claim_is_released_before_asking_for_the_next_page()
        {
            var registry = new HistoryOnDemandRegistry();
            Assert.True(registry.TryClaimFull());

            registry.ReleaseFullIfUntracked();
            Assert.True(registry.TryClaimFull());

            registry.RegisterFull("F1", false, 0, "r", T0);
            registry.ReleaseFullIfUntracked();
            Assert.True(registry.IsFullInFlight);
        }

        // --- Catch-up progress -------------------------------------------------

        [Fact]
        public void Lot_progress_bumps_the_signal_count_and_the_idle_clock()
        {
            var registry = new HistoryOnDemandRegistry();
            var request = StartFull(registry);

            Assert.True(registry.NoteFullProgress("chunk", countsAsLot: true, T0.AddSeconds(5), out string id, out int signals));

            Assert.Equal("F1", id);
            Assert.Equal(1, signals);
            Assert.Equal(T0.AddSeconds(5), request.LastProgressUtc);
            Assert.Equal("chunk", request.LastProgressReason);
        }

        [Fact]
        public void An_ack_only_refreshes_the_idle_clock()
        {
            var registry = new HistoryOnDemandRegistry();
            var request = StartFull(registry);

            registry.NoteFullProgress("ack-accepted", countsAsLot: false, T0.AddSeconds(5), out _, out int signals);

            Assert.Equal(0, signals);
            Assert.Equal(T0.AddSeconds(5), request.LastProgressUtc);
        }

        [Fact]
        public void Progress_with_no_full_request_is_not_recorded()
        {
            var registry = new HistoryOnDemandRegistry();
            StartChat(registry);

            Assert.False(registry.NoteFullProgress("chunk", true, T0, out _, out _));
        }

        // --- Quiet SQLite lot --------------------------------------------------

        [Fact]
        public void A_quiet_lot_hands_the_pending_full_request_to_the_continue_decision()
        {
            var registry = new HistoryOnDemandRegistry();
            var request = StartFull(registry);
            registry.Ack("F1", null, T0);
            registry.NoteFullProgress("chunk", true, T0, out _, out _);

            Assert.True(registry.TryHandOffQuietLot(catchUpActive: false, out string id, out HistoryOnDemandRequest? pending));

            Assert.Equal("F1", id);
            Assert.NotNull(pending);
            Assert.True(pending!.AckAccepted);
            Assert.Equal(1, pending.ProgressSignalCount);
            Assert.False(registry.IsFullClaimed);
            Assert.Null(registry.Find(request.RequestId));
        }

        [Fact]
        public void A_quiet_lot_during_catch_up_with_nothing_tracked_still_hands_off_and_releases()
        {
            var registry = new HistoryOnDemandRegistry();
            Assert.True(registry.TryClaimFull());

            Assert.True(registry.TryHandOffQuietLot(catchUpActive: true, out _, out HistoryOnDemandRequest? pending));

            Assert.Null(pending);
            Assert.False(registry.IsFullClaimed);
        }

        [Fact]
        public void A_quiet_lot_outside_catch_up_with_nothing_pending_is_not_handed_off()
        {
            var registry = new HistoryOnDemandRegistry();

            Assert.False(registry.TryHandOffQuietLot(catchUpActive: false, out _, out _));
        }

        // --- Deferring avatar fetches ------------------------------------------

        [Fact]
        public void A_tracked_full_request_defers_avatar_fetches()
        {
            var registry = new HistoryOnDemandRegistry();
            StartFull(registry);

            Assert.Equal("full-history-on-demand-in-flight:F1", registry.ProbeAvatarDeferral(out string? stale));
            Assert.Null(stale);
        }

        [Fact]
        public void An_untracked_full_id_is_cleared_instead_of_deferring_forever()
        {
            var registry = new HistoryOnDemandRegistry();
            StartFull(registry);
            // Leaves the full id and the claim behind with nothing tracked under them.
            registry.ClearRequests();

            Assert.Null(registry.ProbeAvatarDeferral(out string? stale));
            Assert.NotNull(stale);
            Assert.False(registry.IsFullClaimed);
        }

        [Fact]
        public void A_chat_request_in_flight_defers_avatar_fetches()
        {
            var registry = new HistoryOnDemandRegistry();
            StartChat(registry);

            Assert.Equal("history-on-demand-in-flight:1", registry.ProbeAvatarDeferral(out _));
        }

        // --- Diagnostics and resets --------------------------------------------

        [Fact]
        public void Diagnostics_read_a_copy_of_the_tracked_full_request()
        {
            var registry = new HistoryOnDemandRegistry();
            var request = StartFull(registry);

            registry.ReadFullDiagnostics(out bool pending, out string id, out HistoryOnDemandRequest? copy);

            Assert.True(pending);
            Assert.Equal("F1", id);
            Assert.NotSame(request, copy);
            Assert.Equal("offline-stale", copy!.TriggerReason);
        }

        [Fact]
        public void Clearing_requests_keeps_backoff_and_the_full_claim()
        {
            var registry = new HistoryOnDemandRegistry();
            StartChat(registry);
            registry.Ack("R1", "479", T0);
            StartFull(registry);

            registry.ClearRequests();

            Assert.Null(registry.Find("F1"));
            Assert.True(registry.IsFullClaimed);
            Assert.Equal(HistoryOnDemandChatGate.BackedOff, registry.TryBeginChat(Chat, "M9", T0, out _));

            registry.ClearBackoff();
            registry.ReleaseFull();
            Assert.Equal(HistoryOnDemandChatGate.Started, registry.TryBeginChat(Chat, "M9", T0, out _));
            Assert.False(registry.IsFullClaimed);
        }
    }
}
