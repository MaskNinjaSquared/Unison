using System.Threading.Tasks;
using Unison.WhatsAppClient.Connection;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class InitGateOwnerTests
    {
        [Fact]
        public async Task Enter_serializes_until_exit()
        {
            var gate = new InitGateOwner();

            await gate.EnterAsync();
            Task second = gate.EnterAsync();
            Assert.False(second.IsCompleted);

            gate.Exit();
            await second;
            gate.Exit();
        }
    }
}
