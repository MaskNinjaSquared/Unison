// =============================================================================
// Tests for MessageMentionsSelf.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MessageMentionsSelfTests
    {
        [Fact]
        public void Mention_all_is_always_a_citation()
        {
            Assert.True(MessageMentionsSelf.Check(true, null, _ => false));
        }

        [Fact]
        public void Mentioned_self_jid_is_a_citation()
        {
            Assert.True(MessageMentionsSelf.Check(
                false,
                new[] { "other@s.whatsapp.net", "me@s.whatsapp.net" },
                jid => string.Equals(jid, "me@s.whatsapp.net", StringComparison.OrdinalIgnoreCase)));
        }

        [Fact]
        public void Mentions_of_others_only_are_not_citations()
        {
            Assert.False(MessageMentionsSelf.Check(
                false,
                new[] { "other@s.whatsapp.net" },
                jid => string.Equals(jid, "me@s.whatsapp.net", StringComparison.OrdinalIgnoreCase)));
        }

        [Fact]
        public void Blank_mentions_and_null_predicate_are_false()
        {
            Assert.False(MessageMentionsSelf.Check(false, new[] { " ", null }, null));
            Assert.False(MessageMentionsSelf.Check(false, null, _ => true));
        }
    }
}
