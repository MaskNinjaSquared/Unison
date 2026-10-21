// =============================================================================
// Tests for SyncWorkGate: whether background name, picture and group work
// should step aside for replay or history traffic.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class SyncWorkGateTests
    {
        private const string Chat = "5511999990001@s.whatsapp.net";

        private readonly HistoryOnDemandRegistry _history = new HistoryOnDemandRegistry();
        private bool _awaitingInitialSync;
        private bool _historyCatchUpActive;
        private bool _firstSyncUiHold;

        private SyncWorkGate Create() =>
            new SyncWorkGate(
                _history,
                () => _awaitingInitialSync,
                () => _historyCatchUpActive,
                () => _firstSyncUiHold);

        [Fact]
        public void Nothing_waits_on_a_quiet_connection()
        {
            SyncWorkGate gate = Create();

            Assert.False(gate.IsReplayDrainActive);
            Assert.False(gate.ShouldDeferAvatarFetch(out string reason));
            Assert.Null(reason);
        }

        [Fact]
        public void A_pending_replay_drain_holds_work_until_it_is_released()
        {
            SyncWorkGate gate = Create();

            gate.SetReplayDrainPending(true);
            Assert.True(gate.IsReplayDrainActive);

            gate.SetReplayDrainPending(false);
            Assert.False(gate.IsReplayDrainActive);
        }

        [Fact]
        public void A_socket_still_awaiting_initial_sync_counts_as_replay()
        {
            SyncWorkGate gate = Create();
            _awaitingInitialSync = true;

            Assert.True(gate.IsReplayDrainActive);
        }

        [Fact]
        public void A_socket_that_cannot_answer_does_not_hold_work()
        {
            var gate = new SyncWorkGate(_history, () => throw new ObjectDisposedException("socket"));

            Assert.False(gate.IsReplayDrainActive);
        }

        [Fact]
        public void Pictures_wait_for_replay_first()
        {
            SyncWorkGate gate = Create();
            gate.SetReplayDrainPending(true);
            _history.TryBeginChat(Chat, "m1", DateTime.UtcNow, out _);

            Assert.True(gate.ShouldDeferAvatarFetch(out string reason));
            Assert.Equal("reconnect-replay-active", reason);
        }

        [Fact]
        public void Pictures_wait_for_history_on_demand()
        {
            SyncWorkGate gate = Create();
            _history.TryBeginChat(Chat, "m1", DateTime.UtcNow, out _);

            Assert.True(gate.ShouldDeferAvatarFetch(out string reason));
            Assert.Equal("history-on-demand-in-flight:1", reason);
        }

        [Fact]
        public void Missing_dependencies_are_rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new SyncWorkGate(null, () => false));
            Assert.Throws<ArgumentNullException>(() => new SyncWorkGate(_history, null));
        }

        [Fact]
        public void An_active_history_catch_up_defers_heavy_local_work()
        {
            // Post-reconnect with catch-up still owed: preview tip-scan and usync must wait.
            SyncWorkGate gate = Create();
            _historyCatchUpActive = true;

            Assert.True(gate.ShouldDeferHeavyLocalWork(out string reason));
            Assert.Equal("history-catch-up", reason);
            Assert.True(gate.ShouldDeferAvatarFetch(out string avatarReason));
            Assert.Equal("history-catch-up", avatarReason);
        }

        [Fact]
        public void Catch_up_defer_wins_over_a_pending_replay_drain()
        {
            SyncWorkGate gate = Create();
            gate.SetReplayDrainPending(true);
            _historyCatchUpActive = true;

            Assert.True(gate.ShouldDeferHeavyLocalWork(out string reason));
            Assert.Equal("history-catch-up", reason);
        }

        [Fact]
        public void A_catch_up_probe_that_throws_does_not_hold_work()
        {
            var gate = new SyncWorkGate(
                _history,
                () => false,
                () => throw new ObjectDisposedException("catch-up"));

            Assert.False(gate.ShouldDeferHeavyLocalWork(out string reason));
            Assert.Null(reason);
        }

        [Fact]
        public void Pictures_wait_for_first_sync_ui_hold_before_unlock()
        {
            SyncWorkGate gate = Create();
            _firstSyncUiHold = true;

            Assert.True(gate.ShouldDeferAvatarFetch(out string reason));
            Assert.Equal(FirstSyncUiAvatarPolicy.DeferReason, reason);
            // Names / groups may still run — only network photos follow unlock.
            Assert.False(gate.ShouldDeferHeavyLocalWork(out _));
        }

        [Fact]
        public void First_sync_ui_hold_wins_over_catch_up_reason_for_pictures()
        {
            SyncWorkGate gate = Create();
            _firstSyncUiHold = true;
            _historyCatchUpActive = true;

            Assert.True(gate.ShouldDeferAvatarFetch(out string reason));
            Assert.Equal(FirstSyncUiAvatarPolicy.DeferReason, reason);
        }

        [Fact]
        public void A_first_sync_hold_probe_that_throws_does_not_hold_pictures()
        {
            var gate = new SyncWorkGate(
                _history,
                () => false,
                () => false,
                () => throw new ObjectDisposedException("history"));

            Assert.False(gate.ShouldDeferAvatarFetch(out string reason));
            Assert.Null(reason);
        }
    }
}
