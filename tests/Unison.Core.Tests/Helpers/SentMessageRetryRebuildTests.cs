using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SentMessageRetryRebuildTests
    {
        [Fact]
        public void Rebuilds_outgoing_text()
        {
            var stored = new ChatMessage
            {
                IsFromMe = true,
                Kind = ChatMessageKind.Text,
                Content = "hello"
            };

            global::Proto.Message rebuilt = SentMessageRetryRebuild.TryCreate(stored);

            Assert.NotNull(rebuilt);
            Assert.Equal("hello", rebuilt.Conversation);
        }

        [Fact]
        public void Declines_media_and_incoming()
        {
            Assert.Null(SentMessageRetryRebuild.TryCreate(new ChatMessage
            {
                IsFromMe = true,
                Kind = ChatMessageKind.Image,
                Content = "keys"
            }));

            Assert.Null(SentMessageRetryRebuild.TryCreate(new ChatMessage
            {
                IsFromMe = false,
                Kind = ChatMessageKind.Text,
                Content = "hello"
            }));

            Assert.Null(SentMessageRetryRebuild.TryCreate(null));
        }
    }
}
