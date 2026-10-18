using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>
    /// Search has to hit the name the list strip paints: the group author, and
    /// an @mention rewritten from the roster (or from the contact service when
    /// the roster has no name). A member who is only on the roster is not on
    /// the strip, so their name must not match.
    /// </summary>
    public class ChatListQueryTests
    {
        [Fact]
        public void The_author_prefix_matches_without_asking_the_contact_service()
        {
            var chat = new ChatItem
            {
                Name = "Família",
                JID = "1@g.us",
                LastMessage = "cheguei",
                LastMessageAuthor = "Beatriz: ",
                LastMessageSenderName = "Beatriz"
            };

            int chatResolves = 0;
            int mentionResolves = 0;

            bool matched = ChatListQuery.Matches(
                chat,
                "beatriz",
                () =>
                {
                    chatResolves++;
                    return "outro";
                },
                jid =>
                {
                    mentionResolves++;
                    return jid;
                });

            Assert.True(matched);
            Assert.Equal(0, chatResolves);
            Assert.Equal(0, mentionResolves);
        }

        [Fact]
        public void A_roster_mention_matches_and_a_member_who_is_not_on_the_strip_does_not()
        {
            var chat = GroupWith(
                "oi @5511999999999",
                new[] { "5511999999999@s.whatsapp.net" },
                Member("5511999999999@s.whatsapp.net", "5511999999999", "Ana Costa"),
                Member("5521988887777@s.whatsapp.net", "5521988887777", "Carlos"));

            int mentionResolves = 0;

            Assert.True(ChatListQuery.Matches(chat, "ana", () => null, CountMentions));
            Assert.False(ChatListQuery.Matches(chat, "carlos", () => null, CountMentions));
            Assert.Equal(0, mentionResolves);

            string CountMentions(string jid)
            {
                mentionResolves++;
                return null;
            }
        }

        [Fact]
        public void A_mention_the_roster_cannot_name_uses_the_contact_service_once()
        {
            var chat = new ChatItem
            {
                Name = "Família",
                JID = "1@g.us",
                LastMessage = "oi @5511888888888",
                LastMessageMentionedJids = new List<string> { "5511888888888@lid" }
            };

            var asked = new List<string>();

            bool matched = ChatListQuery.Matches(
                chat,
                "diego",
                () =>
                {
                    asked.Add("chat");
                    return null;
                },
                jid =>
                {
                    asked.Add(jid);
                    return jid.EndsWith("@s.whatsapp.net") ? "Diego" : null;
                });

            Assert.True(matched);
            Assert.Equal(new[] { "5511888888888@s.whatsapp.net" }, asked);
        }

        [Fact]
        public void The_conversation_name_is_resolved_only_when_the_strip_does_not_match()
        {
            var chat = new ChatItem
            {
                Name = "5511999999999",
                JID = "5511999999999@s.whatsapp.net",
                LastMessage = "oi"
            };

            int calls = 0;
            bool matched = ChatListQuery.Matches(
                chat,
                "marina",
                () =>
                {
                    calls++;
                    return "~Marina";
                },
                _ => null);

            Assert.True(matched);
            Assert.Equal(1, calls);
        }

        private static ChatItem GroupWith(string lastMessage, string[] mentioned, params GroupMember[] members)
        {
            return new ChatItem
            {
                Name = "Família",
                JID = "1@g.us",
                LastMessage = lastMessage,
                LastMessageMentionedJids = new List<string>(mentioned),
                GroupMembers = new List<GroupMember>(members)
            };
        }

        private static GroupMember Member(string jid, string phone, string name)
        {
            return new GroupMember
            {
                Jid = jid,
                PhoneNumber = phone,
                DisplayName = name
            };
        }
    }
}
