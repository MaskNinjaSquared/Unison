using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>
    /// The @ menu always offers everyone, then filters members by name.
    /// The field keeps the readable mention. Send turns a person into @number
    /// plus the JID, and everyone into the flag rather than a JID.
    /// </summary>
    public class ComposerMentionDraftTests
    {
        [Fact]
        public void Typing_at_offers_everyone_and_filters_members_by_name()
        {
            var members = new[]
            {
                Member("5511999999999@s.whatsapp.net", "Ana Costa", "5511999999999"),
                Member("5521988887777@s.whatsapp.net", "Carlos", "5521988887777")
            };

            Assert.True(MentionSearch.TryReadToken("oi @an", 6, out int at, out string query, out int length));
            Assert.Equal(3, at);
            Assert.Equal("an", query);
            Assert.Equal(3, length);

            var rows = MentionSearch.Search(members, query, "Todos", "Mencione todos os membros do grupo", null);

            Assert.True(rows[0].IsAll);
            Assert.Equal("Todos", rows[0].Name);
            Assert.Equal(2, rows.Count);
            Assert.Equal("Ana Costa", rows[1].Name);
            Assert.Equal("5511999999999@s.whatsapp.net", rows[1].Jid);
        }

        [Fact]
        public void Search_leaves_the_logged_in_member_out()
        {
            var members = new[]
            {
                Member("5511999999999@s.whatsapp.net", "Ana Costa", "5511999999999"),
                Member("5511888888888@s.whatsapp.net", "Eu", "5511888888888")
            };

            var rows = MentionSearch.Search(
                members,
                string.Empty,
                "Todos",
                "Mencione todos os membros do grupo",
                null,
                member => member.Jid == "5511888888888@s.whatsapp.net");

            Assert.Equal(2, rows.Count);
            Assert.True(rows[0].IsAll);
            Assert.Equal("Ana Costa", rows[1].Name);
        }

        [Fact]
        public void An_at_inside_a_word_does_not_open_the_menu()
        {
            Assert.False(MentionSearch.TryReadToken("email@ana", 9, out _, out _, out _));
        }

        [Fact]
        public void Choosing_a_person_sends_the_number_and_the_jid()
        {
            var draft = new ComposerMentionDraft();
            draft.SetText("oi @an");

            string shown = draft.Insert(
                new MentionChoice { Name = "Ana Costa", Jid = "5511999999999@s.whatsapp.net" },
                caret: 6,
                tokenLength: 3);

            Assert.Equal("oi @Ana Costa ", shown);
            OutgoingTextMentions wire = draft.GetMessage();
            Assert.Equal("oi @5511999999999 ", wire.Text);
            Assert.Equal(new[] { "5511999999999@s.whatsapp.net" }, wire.Mentions);
            Assert.False(wire.MentionAll);
        }

        [Fact]
        public void Choosing_everyone_sets_the_flag_and_keeps_the_localized_word()
        {
            var draft = new ComposerMentionDraft();
            draft.SetText("@");

            draft.Insert(new MentionChoice { IsAll = true, Name = "Todos" }, caret: 1, tokenLength: 1);

            OutgoingTextMentions wire = draft.GetMessage();
            Assert.Equal("@Todos ", wire.Text);
            Assert.Empty(wire.Mentions);
            Assert.True(wire.MentionAll);
            Assert.True(draft.MentionAll);
        }

        [Fact]
        public void Backspace_inside_a_mention_removes_the_whole_mention()
        {
            var draft = new ComposerMentionDraft();
            draft.SetText("@");
            draft.Insert(new MentionChoice { Name = "Ana Costa", Jid = "5511999999999@s.whatsapp.net" }, 1, 1);

            // "@Ana Costa " — delete the 'C'.
            string shown = draft.SetText("@Ana osta ");

            Assert.Equal(string.Empty, shown.Trim());
            OutgoingTextMentions wire = draft.GetMessage();
            Assert.Empty(wire.Mentions);
            Assert.False(wire.MentionAll);
        }

        [Fact]
        public void Removing_everyone_clears_the_flag()
        {
            var draft = new ComposerMentionDraft();
            draft.SetText("@");
            draft.Insert(new MentionChoice { IsAll = true, Name = "Todos" }, 1, 1);

            draft.SetText("@Todo ");

            Assert.False(draft.MentionAll);
        }

        private static GroupMember Member(string jid, string name, string phone)
        {
            return new GroupMember
            {
                Jid = jid,
                DisplayName = name,
                PhoneNumber = phone
            };
        }
    }
}
