using System.Threading.Tasks;
using Unison.WhatsAppClient.Connection;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionGateLocksTests
    {
        [Fact]
        public async Task Connect_gate_serializes_enter()
        {
            var gates = new ConnectionGateLocks();

            await gates.EnterConnectAsync();
            Task second = gates.EnterConnectAsync();
            Assert.False(second.IsCompleted);

            gates.ExitConnect();
            await second;
            gates.ExitConnect();
        }

        [Fact]
        public async Task Resume_gate_is_independent_of_connect()
        {
            var gates = new ConnectionGateLocks();

            await gates.EnterConnectAsync();
            Assert.True(await gates.ResumeGate.WaitAsync(0));
            gates.ResumeGate.Release();
            gates.ExitConnect();
        }
    }
}
