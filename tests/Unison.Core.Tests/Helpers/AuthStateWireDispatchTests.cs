using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class AuthStateWireDispatchTests
    {
        [Fact]
        public async Task Run_files_aliases_then_persists()
        {
            var host = new FakeHost();

            await AuthStateWireDispatch.RunAsync(host);

            Assert.Equal(
                new[]
                {
                    "log-updated",
                    "aliases:auth-update-identity",
                    "persist"
                },
                host.Log);
        }

        private sealed class FakeHost : IAuthStateWireHost
        {
            public List<string> Log { get; } = new List<string>();

            public void LogAuthStateUpdated() => Log.Add("log-updated");

            public void FileSelfIdentityAliases(string source) =>
                Log.Add("aliases:" + source);

            public Task PersistAuthStateFromWireAsync()
            {
                Log.Add("persist");
                return Task.CompletedTask;
            }
        }
    }
}
