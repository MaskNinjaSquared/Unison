// =============================================================================
// GroupSubjectRowWrite — Name moves only when blacklist allows and != current.
// =============================================================================
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupSubjectRowWriteTests
    {
        private const string GroupJid = "120363000000000001@g.us";

        [Fact]
        public void Writes_when_incoming_meaningful_and_differs()
        {
            var chat = new ChatItem { JID = GroupJid, Name = "120363000000000001" };

            bool changed = GroupSubjectRowWrite.TryWrite(
                chat,
                "Team Unison",
                incomingMeaningful: true,
                existingMeaningful: false);

            Assert.True(changed);
            Assert.Equal("Team Unison", chat.Name);
        }

        [Fact]
        public void Returns_false_when_subject_equals_current_name()
        {
            var chat = new ChatItem { JID = GroupJid, Name = "Team Unison" };

            bool changed = GroupSubjectRowWrite.TryWrite(
                chat,
                "Team Unison",
                incomingMeaningful: true,
                existingMeaningful: true);

            Assert.False(changed);
            Assert.Equal("Team Unison", chat.Name);
        }

        [Fact]
        public void Rejects_blacklisted_subject_over_usable_name()
        {
            var chat = new ChatItem { JID = GroupJid, Name = "Team Unison" };

            bool changed = GroupSubjectRowWrite.TryWrite(
                chat,
                "invite link group",
                incomingMeaningful: true,
                existingMeaningful: true);

            Assert.False(changed);
            Assert.Equal("Team Unison", chat.Name);
        }

        [Fact]
        public void Null_chat_is_noop()
        {
            Assert.False(GroupSubjectRowWrite.TryWrite(
                null,
                "Team",
                incomingMeaningful: true,
                existingMeaningful: false));
        }
    }
}
