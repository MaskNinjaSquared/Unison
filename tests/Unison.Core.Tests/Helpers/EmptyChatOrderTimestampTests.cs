// =============================================================================
// Empty chats sort by activity / join / creation — never invent a tip over a real one.
// =============================================================================
using System;
using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class EmptyChatOrderTimestampTests
    {
        private static readonly DateTime Tip =
            new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime Conversation =
            new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime Created =
            new DateTime(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime Creation =
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime Fallback =
            new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Existing_tip_wins()
        {
            DateTime? resolved = EmptyChatOrderTimestamp.Resolve(
                Tip,
                conversationTimestampUnix: ToUnix(Conversation),
                hasConversationTimestamp: true,
                createdAtUnix: ToUnix(Created),
                hasCreatedAt: true,
                groupCreationUnixSeconds: ToUnix(Creation),
                fallbackUtc: Fallback);

            Assert.Equal(Tip, resolved);
        }

        [Fact]
        public void Conversation_timestamp_before_created_at()
        {
            DateTime? resolved = EmptyChatOrderTimestamp.Resolve(
                existingTipUtc: null,
                conversationTimestampUnix: ToUnix(Conversation),
                hasConversationTimestamp: true,
                createdAtUnix: ToUnix(Created),
                hasCreatedAt: true,
                groupCreationUnixSeconds: ToUnix(Creation),
                fallbackUtc: Fallback);

            Assert.Equal(Conversation, resolved);
        }

        [Fact]
        public void Created_at_before_group_creation()
        {
            DateTime? resolved = EmptyChatOrderTimestamp.Resolve(
                existingTipUtc: null,
                conversationTimestampUnix: 0,
                hasConversationTimestamp: false,
                createdAtUnix: ToUnix(Created),
                hasCreatedAt: true,
                groupCreationUnixSeconds: ToUnix(Creation),
                fallbackUtc: Fallback);

            Assert.Equal(Created, resolved);
        }

        [Fact]
        public void Group_creation_before_fallback()
        {
            DateTime? resolved = EmptyChatOrderTimestamp.Resolve(
                existingTipUtc: null,
                groupCreationUnixSeconds: ToUnix(Creation),
                fallbackUtc: Fallback);

            Assert.Equal(Creation, resolved);
        }

        [Fact]
        public void Fallback_when_wire_is_empty()
        {
            DateTime? resolved = EmptyChatOrderTimestamp.Resolve(
                existingTipUtc: null,
                fallbackUtc: Fallback);

            Assert.Equal(Fallback, resolved);
        }

        [Fact]
        public void Null_when_nothing_available()
        {
            Assert.Null(EmptyChatOrderTimestamp.Resolve(existingTipUtc: null));
        }

        [Fact]
        public void Zero_unix_is_ignored()
        {
            Assert.Null(EmptyChatOrderTimestamp.FromUnixSeconds(0L));
            Assert.Null(EmptyChatOrderTimestamp.FromUnixSeconds(0UL));

            DateTime? resolved = EmptyChatOrderTimestamp.Resolve(
                existingTipUtc: null,
                conversationTimestampUnix: 0,
                hasConversationTimestamp: true,
                fallbackUtc: Fallback);

            Assert.Equal(Fallback, resolved);
        }

        [Fact]
        public void Has_flag_false_skips_zeroed_conversation_field()
        {
            // Proto default 0 without Has* must not beat CreatedAt.
            DateTime? resolved = EmptyChatOrderTimestamp.Resolve(
                existingTipUtc: null,
                conversationTimestampUnix: 0,
                hasConversationTimestamp: false,
                createdAtUnix: ToUnix(Created),
                hasCreatedAt: true);

            Assert.Equal(Created, resolved);
        }

        private static long ToUnix(DateTime utc) =>
            new DateTimeOffset(utc).ToUnixTimeSeconds();
    }
}
