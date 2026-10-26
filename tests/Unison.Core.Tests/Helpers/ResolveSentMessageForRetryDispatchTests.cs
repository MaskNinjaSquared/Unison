using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ResolveSentMessageForRetryDispatchTests
    {
        [Fact]
        public async Task Rebuilds_text_from_persisted_row()
        {
            var host = new FakeHost
            {
                HasStore = true,
                Stored = new ChatMessage
                {
                    IsFromMe = true,
                    Kind = ChatMessageKind.Text,
                    Content = "hello"
                }
            };
            global::Proto.Message rebuilt = await ResolveSentMessageForRetryDispatch.RunAsync(
                host,
                "111@s.whatsapp.net",
                "abc");
            Assert.Equal("hello", rebuilt.Conversation);
        }

        [Fact]
        public async Task Declines_non_text_from_me()
        {
            var host = new FakeHost
            {
                HasStore = true,
                Stored = new ChatMessage
                {
                    IsFromMe = true,
                    Kind = ChatMessageKind.Image,
                    Content = "x"
                }
            };
            Assert.Null(await ResolveSentMessageForRetryDispatch.RunAsync(host, "111@s.whatsapp.net", "abc"));
            Assert.NotNull(host.DeclinedKind);
        }

        [Fact]
        public async Task Lookup_failure_is_logged()
        {
            var host = new FakeHost { HasStore = true, ThrowOnFind = true };
            Assert.Null(await ResolveSentMessageForRetryDispatch.RunAsync(host, "111@s.whatsapp.net", "abc"));
            Assert.NotNull(host.LoggedFailure);
        }

        private sealed class FakeHost : IResolveSentMessageForRetryHost
        {
            public bool HasStore;
            public ChatMessage Stored;
            public bool ThrowOnFind;
            public ChatMessageKind? DeclinedKind;
            public string LoggedFailure;

            public bool HasMessageStore => HasStore;

            public string NormalizeJid(string remoteJid) => remoteJid;

            public Task<ChatMessage> FindPersistedChatMessageAsync(string chatJid, string messageId)
            {
                if (ThrowOnFind)
                {
                    throw new System.InvalidOperationException("find");
                }

                return Task.FromResult(Stored);
            }

            public void LogRetryLookupDeclined(string messageId, ChatMessageKind kind) =>
                DeclinedKind = kind;

            public void LogRetryLookupFailed(string messageId, string message) =>
                LoggedFailure = message;
        }
    }
}
