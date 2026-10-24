using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PersistSelfAvatarUrlDispatchTests
    {
        [Fact]
        public void Skips_when_no_self_me()
        {
            var host = new FakeHost { HasSelf = false };
            PersistSelfAvatarUrlDispatch.Run(host, "https://x");
            Assert.Null(host.Avatar);
            Assert.False(host.Saved);
        }

        [Fact]
        public void Skips_when_unchanged()
        {
            var host = new FakeHost { HasSelf = true, Avatar = "https://same" };
            PersistSelfAvatarUrlDispatch.Run(host, "https://same");
            Assert.False(host.Saved);
        }

        [Fact]
        public async Task Persists_when_changed()
        {
            var host = new FakeHost { HasSelf = true, CanPersist = true };
            PersistSelfAvatarUrlDispatch.Run(host, "https://new");
            Assert.Equal("https://new", host.Avatar);
            await Task.Delay(50);
            Assert.True(host.Saved);
        }

        [Fact]
        public async Task Save_failure_is_logged()
        {
            var host = new FakeHost { HasSelf = true, CanPersist = true, ThrowOnSave = true };
            await PersistSelfAvatarUrlDispatch.RunSaveAsync(host);
            Assert.NotNull(host.LoggedError);
        }

        private sealed class FakeHost : IPersistSelfAvatarUrlHost
        {
            public bool HasSelf;
            public string Avatar;
            public bool CanPersist;
            public bool Saved;
            public bool ThrowOnSave;
            public string LoggedError;

            bool IPersistSelfAvatarUrlHost.HasSelfMe => HasSelf;

            string IPersistSelfAvatarUrlHost.SelfMeAvatarUrl => Avatar;

            void IPersistSelfAvatarUrlHost.SetSelfMeAvatarUrl(string avatarUrl) => Avatar = avatarUrl;

            bool IPersistSelfAvatarUrlHost.CanPersistAuth => CanPersist;

            Task IPersistSelfAvatarUrlHost.SaveAuthStateAsync()
            {
                if (ThrowOnSave)
                {
                    throw new System.InvalidOperationException("save");
                }

                Saved = true;
                return Task.CompletedTask;
            }

            void IPersistSelfAvatarUrlHost.LogSelfAvatarPersistFailed(string message) =>
                LoggedError = message;
        }
    }
}
