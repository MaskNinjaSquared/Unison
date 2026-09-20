// =============================================================================
// Tests for LiveChatPreviewApplier.
//
// The chat-list strip update that every live / offline / revoke path used to
// keep as a private method on WhatsAppService. Previews arrive out of order;
// the rule is "accept when not older, then write body / author / kind / ticks".
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class LiveChatPreviewApplierTests
    {
        private static readonly DateTime Noon =
            new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

        private static ChatItem Row(string lastMessage, DateTime? tipUtc, string messageId = null)
        {
            return new ChatItem
            {
                JID = "120363000000000000@g.us",
                IsGroup = true,
                LastMessage = lastMessage,
                LastMessageTimestampUtc = tipUtc,
                LastMessageId = messageId
            };
        }

        [Fact]
        public void A_newer_tip_replaces_the_strip()
        {
            ChatItem chat = Row("before", Noon);

            bool applied = LiveChatPreviewApplier.ApplyIfNewer(
                chat,
                "after",
                Noon.AddMinutes(1),
                yesterdayLabel: "Yesterday");

            Assert.True(applied);
            Assert.Equal("after", chat.LastMessage);
            Assert.Equal(Noon.AddMinutes(1), chat.LastMessageTimestampUtc);
        }

        [Fact]
        public void An_older_tip_is_ignored()
        {
            ChatItem chat = Row("live", Noon);

            bool applied = LiveChatPreviewApplier.ApplyIfNewer(
                chat,
                "history-echo",
                Noon.AddMinutes(-5),
                yesterdayLabel: "Yesterday");

            Assert.False(applied);
            Assert.Equal("live", chat.LastMessage);
        }

        [Fact]
        public void The_same_tip_with_a_new_body_still_refreshes()
        {
            // Same second + same id often means a caption finished downloading.
            ChatItem chat = Row("old caption", Noon, messageId: "3EB0");
            chat.LastMessageIsFromMe = false;
            chat.LastMessageSendState = MessageSendState.NotApplicable;

            bool applied = LiveChatPreviewApplier.ApplyIfNewer(
                chat,
                "new caption",
                Noon,
                messageId: "3EB0",
                isFromMe: false,
                sendState: MessageSendState.NotApplicable,
                yesterdayLabel: "Yesterday");

            Assert.True(applied);
            Assert.Equal("new caption", chat.LastMessage);
        }

        [Fact]
        public void The_same_tip_with_identical_body_is_a_no_op()
        {
            ChatItem chat = Row("same", Noon, messageId: "3EB0");
            chat.LastMessageIsFromMe = false;
            chat.LastMessageSendState = MessageSendState.NotApplicable;

            bool applied = LiveChatPreviewApplier.ApplyIfNewer(
                chat,
                "same",
                Noon,
                messageId: "3EB0",
                isFromMe: false,
                sendState: MessageSendState.NotApplicable,
                yesterdayLabel: "Yesterday");

            Assert.False(applied);
        }

        [Fact]
        public void An_author_prefix_is_stored_separately_from_the_body()
        {
            ChatItem chat = Row(string.Empty, null);

            LiveChatPreviewApplier.ApplyIfNewer(
                chat,
                "hello",
                Noon,
                authorPrefix: "Ana: ",
                yesterdayLabel: "Yesterday");

            Assert.Equal("Ana: ", chat.LastMessageAuthor);
            Assert.Equal("hello", chat.LastMessage);
        }

        [Fact]
        public void Force_writes_even_when_the_candidate_looks_older()
        {
            ChatItem chat = Row("wrong tip", Noon);

            bool applied = LiveChatPreviewApplier.ApplyIfNewer(
                chat,
                "corrected",
                Noon.AddMinutes(-10),
                force: true,
                yesterdayLabel: "Yesterday");

            Assert.True(applied);
            Assert.Equal("corrected", chat.LastMessage);
        }

        [Fact]
        public void Mentioned_jids_are_copied_onto_the_row()
        {
            ChatItem chat = Row(string.Empty, null);
            var mentions = new List<string> { "5511999999999@s.whatsapp.net" };

            LiveChatPreviewApplier.ApplyIfNewer(
                chat,
                "hi @Ana",
                Noon,
                mentionedJids: mentions,
                yesterdayLabel: "Yesterday");

            Assert.NotNull(chat.LastMessageMentionedJids);
            Assert.Single(chat.LastMessageMentionedJids);
            Assert.Equal("5511999999999@s.whatsapp.net", chat.LastMessageMentionedJids[0]);
        }

        [Fact]
        public void ResolveKind_prefers_a_non_text_render_hint()
        {
            var info = new MessageRenderInfo { IsImage = true };
            var message = new ChatMessage { Kind = ChatMessageKind.Text, Content = "caption" };

            Assert.Equal(ChatPreviewKind.Image, LiveChatPreviewApplier.ResolveKind(message, info));
        }

        [Fact]
        public void ResolveKind_falls_back_to_the_message_when_render_is_plain_text()
        {
            var info = new MessageRenderInfo();
            var message = new ChatMessage { Kind = ChatMessageKind.Voice };

            Assert.Equal(ChatPreviewKind.Voice, LiveChatPreviewApplier.ResolveKind(message, info));
        }
    }
}
