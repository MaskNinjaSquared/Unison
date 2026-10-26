using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ReleaseMemoryDispatchTests
    {
        [Fact]
        public async Task Keeps_active_chat_removes_others()
        {
            var host = new FakeHost
            {
                Active = "a@x",
                Keys = new List<string> { "a@x", "b@x" }
            };
            await ReleaseMemoryDispatch.RunAsync(host);
            Assert.True(host.StoreCleared);
            Assert.Contains("b@x", host.Removed);
            Assert.DoesNotContain("a@x", host.Removed);
            Assert.True(host.LoggedOk);
        }

        [Fact]
        public async Task Failure_is_logged()
        {
            var host = new FakeHost { ThrowOnUi = true };
            await ReleaseMemoryDispatch.RunAsync(host);
            Assert.NotNull(host.LoggedError);
        }

        private sealed class FakeHost : IReleaseMemoryHost
        {
            public string Active;
            public List<string> Keys = new List<string>();
            public List<string> Removed = new List<string>();
            public bool StoreCleared;
            public bool LoggedOk;
            public Exception LoggedError;
            public bool ThrowOnUi;

            public void ClearMessageStoreMemoryCache() => StoreCleared = true;

            public Task RunOnUiAsync(Action action)
            {
                if (ThrowOnUi)
                {
                    throw new InvalidOperationException("ui");
                }

                action();
                return Task.CompletedTask;
            }

            public string ActiveChatJid => Active;

            public IReadOnlyList<string> MessageCacheChatJids() => Keys;

            public string GetCanonicalJid(string jid) => jid;

            public void RemoveMessagesByChatKey(string key) => Removed.Add(key);

            public void LogMemoryReleased() => LoggedOk = true;

            public void LogMemoryReleaseFailed(Exception error) => LoggedError = error;
        }
    }
}
