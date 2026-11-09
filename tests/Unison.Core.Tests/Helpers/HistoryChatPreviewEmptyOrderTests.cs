// =============================================================================
// Empty history conversations keep an order timestamp for the chat list.
// =============================================================================
using System;
using Proto;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistoryChatPreviewEmptyOrderTests
    {
        [Fact]
        public void Flag_only_preview_seeds_conversation_timestamp()
        {
            long conversationUnix = new DateTimeOffset(
                2026, 11, 9, 15, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            var sync = new HistorySync();
            sync.Conversations.Add(new Conversation
            {
                Id = "120363000000000099@g.us",
                Name = "Novo grupo",
                ConversationTimestamp = (ulong)conversationUnix
            });

            var rows = HistoryChatPreviewBuilder.Build(sync, "sync-1");

            Assert.Single(rows);
            HistoryChatPreview row = rows[0];
            Assert.Equal("120363000000000099@g.us", row.Jid);
            Assert.Equal(string.Empty, row.LastMessage);
            Assert.Equal(
                DateTimeOffset.FromUnixTimeSeconds(conversationUnix).UtcDateTime,
                row.LastMessageTimestampUtc);
            Assert.True(HistoryChatPreviewApplier.IsEmptyOrderSeed(row));
            Assert.False(HistoryChatPreviewApplier.IsListable(row));
        }

        [Fact]
        public void Empty_conversation_without_activity_is_skipped()
        {
            var sync = new HistorySync();
            sync.Conversations.Add(new Conversation
            {
                Id = "120363000000000100@g.us",
                Name = "Sem tip"
            });

            Assert.Empty(HistoryChatPreviewBuilder.Build(sync, "sync-1"));
        }

        [Fact]
        public void Catalog_hydrate_keeps_empty_order_timestamp()
        {
            DateTime seed = new DateTime(2026, 11, 9, 16, 0, 0, DateTimeKind.Utc);
            var preview = new HistoryChatPreview
            {
                Jid = "120363000000000101@g.us",
                Name = "Entrada",
                IsGroup = true,
                LastMessage = string.Empty,
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageTimestampUtc = seed,
                Status = ChatStatus.Active
            };

            ChatItem chat = HistoryChatPreviewApplier.ToChatItemForCatalog(preview, "Yesterday");

            Assert.NotNull(chat);
            Assert.Equal(seed, chat.LastMessageTimestampUtc);
            Assert.False(string.IsNullOrEmpty(chat.Timestamp));
        }

        [Fact]
        public void Empty_order_seed_does_not_overwrite_existing_tip()
        {
            DateTime tip = new DateTime(2026, 11, 9, 18, 0, 0, DateTimeKind.Utc);
            DateTime seed = tip.AddHours(-2);
            var target = new ChatItem
            {
                JID = "120363000000000102@g.us",
                LastMessageTimestampUtc = tip,
                LastMessage = "oi"
            };
            var preview = new HistoryChatPreview
            {
                Jid = target.JID,
                LastMessageTimestampUtc = seed,
                LastMessage = string.Empty,
                LastMessageKind = ChatPreviewKind.Text
            };

            Assert.False(HistoryChatPreviewApplier.TryApplyEmptyOrderSeed(preview, target));
            Assert.Equal(tip, target.LastMessageTimestampUtc);
            Assert.Equal("oi", target.LastMessage);
        }

        [Fact]
        public void Empty_order_survives_catalog_round_trip_and_sorts_above_older_tip()
        {
            DateTime now = new DateTime(2026, 11, 9, 19, 0, 0, DateTimeKind.Utc);
            var empty = new ChatItem
            {
                JID = "120363000000000103@g.us",
                Name = "Vazio",
                Kind = ChatKind.Group,
                LastMessage = string.Empty,
                LastMessageTimestampUtc = now
            };
            var older = new ChatItem
            {
                JID = "a@s.whatsapp.net",
                Name = "Older",
                LastMessage = "oi",
                LastMessageTimestampUtc = now.AddDays(-1)
            };

            HistoryChatPreview stored = HistoryChatPreviewApplier.FromChatItem(empty);
            Assert.NotNull(stored);
            Assert.Equal(now, stored.LastMessageTimestampUtc);

            ChatItem restored = HistoryChatPreviewApplier.ToChatItemForCatalog(stored, "Yesterday");
            Assert.Equal(now, restored.LastMessageTimestampUtc);

            var ordered = new System.Collections.Generic.List<ChatItem> { older, restored };
            ordered.Sort(Unison.Core.Helpers.Chats.ChatListRowOrder.Compare);
            Assert.Same(restored, ordered[0]);
        }
    }
}
