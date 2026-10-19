// =============================================================================
// Tests for ChatCatalogSnapshot.
//
// What the three catalog saves (periodic, suspend, identity) take from the live
// chat list on the UI thread. The identity save used to hand-copy a ChatItem per
// row and left out the tip id, the mentions and the group sender, so a row it
// inserted or whose tip it moved first went to disk without them:
//
//   - each row is what HistoryChatPreviewApplier.FromChatItem writes, taken now;
//   - the jid list is every listed address once, normalized, status included;
//   - null rows and blank addresses are skipped; a null list is empty.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatCatalogSnapshotTests
    {
        private const string Pn = "5511999990001@s.whatsapp.net";
        private const string Group = "120363000000000001@g.us";

        [Fact]
        public void RowKeepsTipIdMentionsAndGroupSender()
        {
            var chat = new ChatItem
            {
                JID = Group,
                Name = "Team",
                LastMessage = "@Ana hi",
                LastMessageId = "3EB0ABC",
                LastMessageParticipantJid = Pn,
                LastMessageSenderName = "Ana",
                LastMessageMentionedJids = new List<string> { Pn },
                Status = ChatStatus.Archived
            };

            var snapshot = ChatCatalogSnapshot.Take(new[] { chat });

            HistoryChatPreview row = Assert.Single(snapshot.Rows);
            Assert.Equal("3EB0ABC", row.LastMessageId);
            Assert.Equal(Pn, row.LastMessageParticipantJid);
            Assert.Equal("Ana", row.LastMessageSenderName);
            Assert.Equal(new[] { Pn }, row.LastMessageMentionedJids);
            Assert.Equal(ChatStatus.Archived, row.Status);
        }

        [Fact]
        public void RowIsTakenAtSnapshotTime()
        {
            var chat = new ChatItem { JID = Pn, Name = "Ana", LastMessage = "first" };

            var snapshot = ChatCatalogSnapshot.Take(new[] { chat });
            chat.LastMessage = "second";
            chat.Name = "Changed";

            HistoryChatPreview row = Assert.Single(snapshot.Rows);
            Assert.Equal("first", row.LastMessage);
            Assert.Equal("Ana", row.Name);
        }

        [Fact]
        public void MentionsAreCopiedNotShared()
        {
            var mentions = new List<string> { Pn };
            var chat = new ChatItem { JID = Group, LastMessageMentionedJids = mentions };

            var snapshot = ChatCatalogSnapshot.Take(new[] { chat });
            mentions.Add("other@s.whatsapp.net");

            Assert.Single(snapshot.Rows[0].LastMessageMentionedJids);
        }

        [Fact]
        public void JidsAreNormalizedAndDistinct()
        {
            var chats = new[]
            {
                new ChatItem { JID = "5511999990001:12@s.whatsapp.net" },
                new ChatItem { JID = Pn },
                new ChatItem { JID = Group }
            };

            var snapshot = ChatCatalogSnapshot.Take(chats);

            Assert.Equal(new[] { Pn, Group }, snapshot.ChatJids);
        }

        [Fact]
        public void StatusBroadcastIsListedButNotWritten()
        {
            var chats = new[]
            {
                new ChatItem { JID = "status@broadcast" },
                new ChatItem { JID = Pn }
            };

            var snapshot = ChatCatalogSnapshot.Take(chats);

            HistoryChatPreview row = Assert.Single(snapshot.Rows);
            Assert.Equal(Pn, row.Jid);
            Assert.Contains("status@broadcast", snapshot.ChatJids);
        }

        [Fact]
        public void NullRowsAndBlankAddressesAreSkipped()
        {
            var chats = new[]
            {
                null,
                new ChatItem { JID = "  " },
                new ChatItem { JID = Pn }
            };

            var snapshot = ChatCatalogSnapshot.Take(chats);

            Assert.Single(snapshot.Rows);
            Assert.Equal(new[] { Pn }, snapshot.ChatJids);
        }

        [Fact]
        public void NullListIsEmpty()
        {
            var snapshot = ChatCatalogSnapshot.Take(null);

            Assert.Empty(snapshot.Rows);
            Assert.Empty(snapshot.ChatJids);
        }
    }
}
