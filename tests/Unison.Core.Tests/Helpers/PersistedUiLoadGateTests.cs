using System.Threading.Tasks;
using Unison.WhatsAppClient.Connection;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PersistedUiLoadGateTests
    {
        [Fact]
        public async Task Enter_blocks_second_until_exit()
        {
            var gate = new PersistedUiLoadGate();

            await gate.EnterAsync();
            Task second = gate.EnterAsync();
            Assert.False(second.IsCompleted);

            gate.Exit();
            await second;
            gate.Exit();
        }
    }
}
