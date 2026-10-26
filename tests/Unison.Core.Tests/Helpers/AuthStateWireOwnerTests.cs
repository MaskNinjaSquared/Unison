using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class AuthStateWireOwnerTests
    {
        [Fact]
        public async Task Run_files_aliases_and_persists()
        {
            bool logged = false;
            string filed = null;
            bool persisted = false;
            var owner = new AuthStateWireOwner(
                () => logged = true,
                source => filed = source,
                () =>
                {
                    persisted = true;
                    return Task.CompletedTask;
                });

            await owner.RunAsync();

            Assert.True(logged);
            Assert.Equal("auth-update-identity", filed);
            Assert.True(persisted);
        }
    }
}