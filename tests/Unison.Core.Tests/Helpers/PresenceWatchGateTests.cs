using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PresenceWatchGateTests
    {
        private const string Group = "120363000000000001@g.us";
        private const string Other = "120363000000000002@g.us";

        [Fact]
        public void First_start_restarts()
        {
            var gate = new PresenceWatchGate();

            Assert.True(gate.TryBegin(Group));
        }

        [Fact]
        public void Same_jid_while_watching_does_not_restart()
        {
            var gate = new PresenceWatchGate();
            gate.TryBegin(Group);

            Assert.False(gate.TryBegin(Group));
            Assert.False(gate.TryBegin(" " + Group.ToUpperInvariant() + " "));
        }

        [Fact]
        public void Different_jid_restarts()
        {
            var gate = new PresenceWatchGate();
            gate.TryBegin(Group);

            Assert.True(gate.TryBegin(Other));
        }

        [Fact]
        public void After_stop_same_jid_restarts()
        {
            var gate = new PresenceWatchGate();
            gate.TryBegin(Group);
            gate.Stop();

            Assert.True(gate.TryBegin(Group));
        }

        [Fact]
        public void Blank_jid_never_begins()
        {
            var gate = new PresenceWatchGate();

            Assert.False(gate.TryBegin(null));
            Assert.False(gate.TryBegin("  "));
            Assert.Null(gate.WatchingJid);
        }
    }
}
