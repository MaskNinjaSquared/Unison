using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SelfProfileAuthSyncTests
    {
        [Fact]
        public void SyncFromAuth_applies_normalized_name()
        {
            var profile = new SelfProfileState();
            string currentName = null;
            bool phoneEnsured = false;

            var sync = new SelfProfileAuthSync(
                profile,
                () => true,
                () => "Alice",
                () => currentName,
                value => currentName = value,
                () => null,
                _ => false,
                () => { },
                () => { },
                () => null,
                () => null,
                _ => { },
                () => "5511999999999@s.whatsapp.net",
                () => null,
                _ => { phoneEnsured = true; },
                () => { },
                () => { },
                () => { phoneEnsured = true; },
                _ => { },
                () => false,
                () => Task.CompletedTask);

            sync.SyncFromAuth();

            Assert.Equal("Alice", currentName);
            Assert.True(phoneEnsured);
        }

        [Fact]
        public void SyncFromAuth_noop_without_me()
        {
            var profile = new SelfProfileState();
            string currentName = "keep";
            var sync = new SelfProfileAuthSync(
                profile,
                () => false,
                () => "Alice",
                () => currentName,
                value => currentName = value,
                () => null,
                _ => false,
                () => { },
                () => { },
                () => null,
                () => null,
                _ => { },
                () => null,
                () => null,
                _ => { },
                () => { },
                () => { },
                () => { },
                _ => { },
                () => false,
                () => Task.CompletedTask);

            sync.SyncFromAuth();

            Assert.Equal("keep", currentName);
        }
    }
}
