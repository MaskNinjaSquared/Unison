using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class SessionOpenOnDemandPendingConsumeDispatchTests
    {
        [Fact]
        public async Task Empty_pending_is_noop()
        {
            var host = new FakeHost();
            await SessionOpenOnDemandPendingConsumeDispatch.RunAsync(host, "t");
            Assert.True(host.Took);
            Assert.Empty(host.Ensured);
            Assert.Empty(host.Claimed);
        }

        [Fact]
        public async Task Pairing_bootstrap_defers_without_take()
        {
            var host = new FakeHost
            {
                PairingBootstrapActive = true,
                PeekCount = 2
            };
            await SessionOpenOnDemandPendingConsumeDispatch.RunAsync(host, "offline");
            Assert.False(host.Took);
            Assert.Empty(host.Ensured);
            Assert.Contains(host.Logs, l => l.IndexOf("deferred", System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public async Task Consume_claims_and_ensures_each_jid()
        {
            var host = new FakeHost
            {
                Pending = new List<string> { "111@s.whatsapp.net", "222@lid" }
            };
            await SessionOpenOnDemandPendingConsumeDispatch.RunAsync(host, "session");
            Assert.True(host.Took);
            Assert.Equal(2, host.Claimed.Count);
            Assert.Equal(2, host.Ensured.Count);
            Assert.Equal("111@s.whatsapp.net", host.Ensured[0]);
            Assert.Equal("222@lid", host.Ensured[1]);
        }

        private sealed class FakeHost : ISessionOpenOnDemandPendingConsumeHost
        {
            public bool PairingBootstrapActive;
            public int PeekCount;
            public List<string> Pending = new List<string>();
            public bool Took;
            public List<string> Claimed = new List<string>();
            public List<string> Ensured = new List<string>();
            public List<string> Logs = new List<string>();

            public bool IsPairingBootstrapActive => PairingBootstrapActive;

            public int PeekSessionOpenOnDemandPendingCount() => PeekCount;

            public IReadOnlyList<string> TakeSessionOpenOnDemandPending()
            {
                Took = true;
                List<string> taken = Pending;
                Pending = new List<string>();
                return taken;
            }

            public void LogSessionOpenOnDemandPending(string message) => Logs.Add(message ?? string.Empty);

            public void ClaimSessionOpenOnDemand(string jid) => Claimed.Add(jid);

            public Task EnsureHistoryOnDemandAsync(string jid)
            {
                Ensured.Add(jid);
                return Task.CompletedTask;
            }
        }
    }
}
