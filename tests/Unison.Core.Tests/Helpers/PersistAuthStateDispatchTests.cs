using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PersistAuthStateDispatchTests
    {
        [Fact]
        public async Task Null_auth_logs_skip()
        {
            var host = new FakeHost { Auth = null };
            await PersistAuthStateDispatch.RunAsync(host, null, "boot");
            Assert.Equal("boot", host.SkipReason);
            Assert.False(host.Saved);
        }

        [Fact]
        public async Task Auth_is_assigned_and_saved()
        {
            var auth = new object();
            var host = new FakeHost { Auth = auth };
            await PersistAuthStateDispatch.RunAsync(host, null, "wire");
            Assert.Same(auth, host.SavedAuth);
            Assert.True(host.Saved);
        }

        private sealed class FakeHost : IPersistAuthStateHost
        {
            public object Auth;
            public string SkipReason;
            public bool Saved;
            public object SavedAuth;

            public object ResolveAuthToSave(object sourceSocket) => Auth;

            public void LogSkipAuthStateSave(string reason) => SkipReason = reason;

            public Task AssignAndSaveAuthAsync(object auth)
            {
                SavedAuth = auth;
                Saved = true;
                return Task.CompletedTask;
            }
        }
    }
}
