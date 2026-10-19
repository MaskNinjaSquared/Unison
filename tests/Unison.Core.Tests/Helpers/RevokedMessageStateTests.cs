using System;
using System.Collections.Generic;
using System.ComponentModel;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>
    /// A deleted message is a flag, not a body. The words the user reads are the UI's to choose
    /// in the user's language; nothing below the UI writes or matches them.
    /// </summary>
    public class RevokedMessageStateTests
    {
        private static readonly DateTime At = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_stored_revoke_reads_back_flagged_with_no_body()
        {
            var row = new HistoryMessage
            {
                ChatJid = "1@s.whatsapp.net",
                MessageId = "m1",
                Body = "[Message Deleted]",
                Kind = ChatPreviewKind.Image,
                IsRevoked = true,
                TimestampUtc = At
            };

            ChatMessage message = HistoryMessageMapper.ToChatMessage(row);

            Assert.True(message.IsRevoked);
            Assert.Equal(string.Empty, message.Content);
            Assert.Equal(ChatMessageKind.Text, message.Kind);
        }

        [Fact]
        public void A_revoke_is_written_as_the_flag_with_an_empty_text_row()
        {
            var message = new ChatMessage { Id = "m1", Content = string.Empty, IsRevoked = true, Timestamp = At };

            HistoryMessage row = HistoryLiveMessageMapper.ToHistoryMessage("1@s.whatsapp.net", message);

            Assert.True(row.IsRevoked);
            Assert.Equal(string.Empty, row.Body);
            Assert.Equal(ChatPreviewKind.Text, row.Kind);
        }

        [Fact]
        public void A_cached_message_from_an_older_build_still_carries_its_revoke_into_the_store()
        {
            var message = new ChatMessage { Id = "m1", Content = "[Message Deleted]", Timestamp = At };

            HistoryMessage row = HistoryLiveMessageMapper.ToHistoryMessage("1@s.whatsapp.net", message);

            Assert.True(row.IsRevoked);
            Assert.Equal(string.Empty, row.Body);
        }

        [Fact]
        public void The_list_strip_kind_of_a_revoked_message_is_Revoked()
        {
            var message = new ChatMessage { Id = "m1", IsRevoked = true, Kind = ChatMessageKind.Text };

            Assert.Equal(ChatPreviewKind.Revoked, ChatPreviewNormalizer.InferKindFromMessage(message));
        }

        [Fact]
        public void A_Revoked_strip_drops_the_old_english_marker_from_its_body()
        {
            ChatPreviewNormalizer.Normalize("[Message Deleted]", ChatPreviewKind.Revoked, out var kind, out var text);

            Assert.Equal(ChatPreviewKind.Revoked, kind);
            Assert.Equal(string.Empty, text);
        }

        [Fact]
        public void A_revoked_message_has_no_text_body_to_show()
        {
            var message = new ChatMessage { Kind = ChatMessageKind.Text };
            var raised = new List<string>();
            ((INotifyPropertyChanged)message).PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            message.IsRevoked = true;

            Assert.False(message.ShowTextContent);
            Assert.Contains(nameof(ChatMessage.ShowTextContent), raised);
        }
    }
}
