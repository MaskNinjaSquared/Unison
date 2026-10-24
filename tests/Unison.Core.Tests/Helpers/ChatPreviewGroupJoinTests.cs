using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatPreviewGroupJoinTests
    {
        [Fact]
        public void InferKind_maps_GroupJoin()
        {
            Assert.Equal(
                ChatPreviewKind.GroupJoin,
                ChatPreviewNormalizer.InferKindFromMessage(
                    new ChatMessage { Kind = ChatMessageKind.GroupJoin }));
        }

        [Fact]
        public void Normalize_keeps_plain_system_text()
        {
            ChatPreviewNormalizer.Normalize(
                "Ana entrou no grupo",
                ChatPreviewKind.GroupJoin,
                out ChatPreviewKind kind,
                out string text);

            Assert.Equal(ChatPreviewKind.GroupJoin, kind);
            Assert.Equal("Ana entrou no grupo", text);
        }

        [Fact]
        public void ToPreviewKind_and_FromPreviewKind_roundtrip()
        {
            Assert.Equal(
                ChatPreviewKind.GroupJoin,
                ChatPreviewNormalizer.ToPreviewKind(ChatMessageKind.GroupJoin));
            Assert.Equal(
                ChatMessageKind.GroupJoin,
                ChatPreviewNormalizer.FromPreviewKind(ChatPreviewKind.GroupJoin));
        }
    }
}
