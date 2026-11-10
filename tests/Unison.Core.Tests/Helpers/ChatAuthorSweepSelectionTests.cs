// =============================================================================
// Pending-chat fast path must not walk the catalogue (open-group RefreshChat).
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatAuthorSweepSelectionTests
    {
        [Fact]
        public void CanUsePendingChatFastPath_only_when_pending_chats_alone()
        {
            Assert.True(ChatAuthorSweepSelection.CanUsePendingChatFastPath(
                unnamedPass: false,
                pendingChatCount: 1,
                pendingParticipantCount: 0));
            Assert.False(ChatAuthorSweepSelection.CanUsePendingChatFastPath(
                unnamedPass: true,
                pendingChatCount: 1,
                pendingParticipantCount: 0));
            Assert.False(ChatAuthorSweepSelection.CanUsePendingChatFastPath(
                unnamedPass: false,
                pendingChatCount: 1,
                pendingParticipantCount: 1));
            Assert.False(ChatAuthorSweepSelection.CanUsePendingChatFastPath(
                unnamedPass: false,
                pendingChatCount: 0,
                pendingParticipantCount: 0));
        }

        [Fact]
        public void CollectPendingChats_uses_FindChat_not_catalogue_enumeration()
        {
            const string groupJid = "120363@g.us";
            var target = new ChatItem
            {
                JID = groupJid,
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = "5511988887777@s.whatsapp.net"
            };

            int findCalls = 0;
            ChatItem Find(string jid)
            {
                findCalls++;
                return string.Equals(jid, groupJid, StringComparison.OrdinalIgnoreCase)
                    ? target
                    : null;
            }

            var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupJid };
            List<ChatItem> work = ChatAuthorSweepSelection.CollectPendingChats(
                pending,
                Find,
                jids: null);

            Assert.Single(work);
            Assert.Same(target, work[0]);
            Assert.Equal(1, findCalls);
        }

        [Fact]
        public void BuildDirectNamesForWork_indexes_only_tip_dms()
        {
            const string tip = "5511988887777@s.whatsapp.net";
            var group = new ChatItem
            {
                JID = "120363@g.us",
                IsGroup = true,
                LastMessageParticipantJid = tip
            };
            var dm = new ChatItem { JID = tip, Name = "Ana Souza", IsGroup = false };
            var decoy = new ChatItem
            {
                JID = "5511911112222@s.whatsapp.net",
                Name = "Decoy",
                IsGroup = false
            };

            int findCalls = 0;
            ChatItem Find(string jid)
            {
                findCalls++;
                if (string.Equals(jid, tip, StringComparison.OrdinalIgnoreCase))
                {
                    return dm;
                }

                if (string.Equals(jid, decoy.JID, StringComparison.OrdinalIgnoreCase))
                {
                    return decoy;
                }

                return null;
            }

            Dictionary<string, string> names = ChatAuthorSweepSelection.BuildDirectNamesForWork(
                new List<ChatItem> { group },
                Find,
                jids: null);

            Assert.Equal("Ana Souza", names[tip]);
            Assert.False(names.ContainsKey(decoy.JID));
            Assert.True(findCalls >= 1);
            Assert.True(findCalls <= 3, "tip/canonical/alias only, finds=" + findCalls);
        }

        /// <summary>
        /// The repair's load-bearing route. Persisted chats arrive as collection Adds, which
        /// schedule a per-chat strip, so every restored group row is visited on this path — and a
        /// row that already carries a real name must not be filtered out here, because that is
        /// exactly the desynced row (known name, digits prefix) the sweep has to rewrite.
        /// </summary>
        [Fact]
        public void CollectPendingChats_keeps_a_named_row_that_is_rendering_digits()
        {
            const string groupJid = "120363@g.us";
            var desynced = new ChatItem
            {
                JID = groupJid,
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = "17186831148@s.whatsapp.net",
                LastMessageSenderName = "Lindsay Hefner",
                LastMessageAuthor = "17186831148: "
            };

            var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupJid };
            List<ChatItem> work = ChatAuthorSweepSelection.CollectPendingChats(
                pending,
                jid => string.Equals(jid, groupJid, StringComparison.OrdinalIgnoreCase)
                    ? desynced
                    : null,
                jids: null);

            Assert.Single(work);
            Assert.Same(desynced, work[0]);
        }
    }
}
