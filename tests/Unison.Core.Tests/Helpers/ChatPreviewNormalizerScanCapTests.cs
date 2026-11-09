using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatPreviewNormalizerScanCapTests
    {
        [Fact]
        public void Normalize_long_body_matches_short_body_with_same_head()
        {
            string head = "Bom dia pessoal, segue o link da reunião de amanhã cedo\nobrigado";
            string huge = head + new string('x', 200000);

            ChatPreviewNormalizer.Normalize(head + new string('x', 600), null, out _, out string shortText);
            ChatPreviewNormalizer.Normalize(huge, null, out ChatPreviewKind kind, out string hugeText);

            Assert.Equal(ChatPreviewKind.Text, kind);
            Assert.Equal(shortText, hugeText);
            Assert.EndsWith("...", hugeText);
        }

        [Fact]
        public void Normalize_short_body_is_unchanged_by_the_cap()
        {
            ChatPreviewNormalizer.Normalize("oi  tudo\nbem?", null, out _, out string text);

            Assert.Equal("oi tudo bem?", text);
        }

        [Fact]
        public void Normalize_marks_cut_when_cap_drops_text_after_whitespace()
        {
            string body = new string(' ', ChatPreviewNormalizer.RawScanCap) + "a" + new string('\n', 10) + "b";

            ChatPreviewNormalizer.Normalize("x" + body, null, out _, out string text);

            Assert.Equal("x...", text);
        }

        [Fact]
        public void Cap_does_not_split_a_surrogate_pair()
        {
            string body = new string('a', ChatPreviewNormalizer.RawScanCap - 1) + "\uD83D\uDE00" + "tail";

            string capped = ChatPreviewNormalizer.CapForPreviewScan(body);

            Assert.Equal(ChatPreviewNormalizer.RawScanCap - 1, capped.Length);
            Assert.False(char.IsHighSurrogate(capped[capped.Length - 1]));
        }

        [Fact]
        public void Cap_returns_empty_for_null()
        {
            Assert.Equal(string.Empty, ChatPreviewNormalizer.CapForPreviewScan(null));
        }
    }
}
