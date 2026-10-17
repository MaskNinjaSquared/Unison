// =============================================================================
// Tests for SyncWorkGate: whether background name, picture and group work
// should step aside for replay or history traffic.
// =============================================================================
using System;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class SyncWorkGateTests
    {
        private const string Chat = "5511999990001@s.whatsapp.net";

        private readonly HistoryOnDemandRegistry _history = new HistoryOnDemandRegistry();
        private bool _awaitingInitialSync;

        private SyncWorkGate Create() => new SyncWorkGate(_history, () => _awaitingInitialSync);

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
    }
}
