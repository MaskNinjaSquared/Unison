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
            var host = new FakeHost { HasSelfMe = false };
            PersistSelfAvatarUrlDispatch.Run(host, "https://x");
            Assert.Null(host.Avatar);
            Assert.False(host.Saved);
        }

        [Fact]
        public void Skips_when_unchanged()
        {
            var host = new FakeHost { HasSelfMe = true, Avatar = "https://same" };
            PersistSelfAvatarUrlDispatch.Run(host, "https://same");
            Assert.False(host.Saved);
        }

        [Fact]
        public async Task Persists_when_changed()
        {
            var host = new FakeHost { HasSelfMe = true, CanPersist = true };
            PersistSelfAvatarUrlDispatch.Run(host, "https://new");
            Assert.Equal("https://new", host.Avatar);
            await Task.Delay(50);
            Assert.True(host.Saved);
        }

        [Fact]
        public async Task Save_failure_is_logged()
        {
            var host = new FakeHost { HasSelfMe = true, CanPersist = true, ThrowOnSave = true };
            await PersistSelfAvatarUrlDispatch.RunSaveAsync(host);
            Assert.NotNull(host.LoggedError);
        }

        private sealed class FakeHost : IPersistSelfAvatarUrlHost
        {
            public bool HasSelfMe { get; set; }
            public string Avatar;
            public bool CanPersist;
            public bool Saved;
            public bool ThrowOnSave;
            public string LoggedError;

            public string SelfMeAvatarUrl => Avatar;
            public void SetSelfMeAvatarUrl(string avatarUrl) => Avatar = avatarUrl;
            public bool CanPersistAuth => CanPersist;

            public Task SaveAuthStateAsync()
            {
                if (ThrowOnSave)
                {
                    throw new System.InvalidOperationException("save");
                }

                Saved = true;
                return Task.CompletedTask;
            }

            public void LogSelfAvatarPersistFailed(string message) => LoggedError = message;
        }
    }
}
