// =============================================================================
// Tests for MessageDeleteEligibility (WhatsApp FAQ delete rules).
// =============================================================================
using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MessageDeleteEligibilityTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Delete_for_me_allows_own_and_received_bubbles()
        {
            Assert.True(MessageDeleteEligibility.CanDeleteForMe(Bubble(fromMe: true)));
            Assert.True(MessageDeleteEligibility.CanDeleteForMe(Bubble(fromMe: false)));
        }

        [Fact]
        public void Delete_for_me_rejects_revoked_or_idless_rows()
        {
            Assert.False(MessageDeleteEligibility.CanDeleteForMe(
                Bubble(fromMe: true, revoked: true)));
            Assert.False(MessageDeleteEligibility.CanDeleteForMe(
                new ChatMessage { Id = null, IsFromMe = true }));
            Assert.False(MessageDeleteEligibility.CanDeleteForMe(null));
        }

        [Fact]
        public void Delete_for_everyone_allows_own_message_inside_the_window()
        {
            ChatMessage mine = Bubble(fromMe: true, sentAt: Now.AddHours(-12));

            Assert.True(MessageDeleteEligibility.CanDeleteForEveryone(
                mine, isGroupChat: false, viewerIsGroupAdmin: false, utcNow: Now));
        }

        [Fact]
        public void Delete_for_everyone_rejects_own_message_after_the_window()
        {
            ChatMessage mine = Bubble(fromMe: true, sentAt: Now.AddHours(-61));

            Assert.False(MessageDeleteEligibility.CanDeleteForEveryone(
                mine, isGroupChat: false, viewerIsGroupAdmin: false, utcNow: Now));
        }

        [Fact]
        public void Delete_for_everyone_allows_admin_to_revoke_someone_elses_group_message()
        {
            ChatMessage theirs = Bubble(fromMe: false, sentAt: Now.AddHours(-1));

            Assert.True(MessageDeleteEligibility.CanDeleteForEveryone(
                theirs, isGroupChat: true, viewerIsGroupAdmin: true, utcNow: Now));
        }

        [Fact]
        public void Delete_for_everyone_rejects_non_admin_revoking_someone_elses_message()
        {
            ChatMessage theirs = Bubble(fromMe: false, sentAt: Now.AddHours(-1));

            Assert.False(MessageDeleteEligibility.CanDeleteForEveryone(
                theirs, isGroupChat: true, viewerIsGroupAdmin: false, utcNow: Now));
            Assert.False(MessageDeleteEligibility.CanDeleteForEveryone(
                theirs, isGroupChat: false, viewerIsGroupAdmin: true, utcNow: Now));
        }

        [Fact]
        public void Window_boundary_accepts_exactly_sixty_hours()
        {
            Assert.True(MessageDeleteEligibility.IsWithinDeleteForEveryoneWindow(
                Now.AddHours(-60), Now));
            Assert.False(MessageDeleteEligibility.IsWithinDeleteForEveryoneWindow(
                Now.AddHours(-60).AddSeconds(-1), Now));
        }

        private static ChatMessage Bubble(
            bool fromMe,
            bool revoked = false,
            DateTime? sentAt = null)
        {
            return new ChatMessage
            {
                Id = "msg-1",
                IsFromMe = fromMe,
                IsRevoked = revoked,
                Timestamp = sentAt ?? Now.AddMinutes(-5)
            };
        }
    }
}
