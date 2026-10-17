// =============================================================================
// Tests for DisplayNameResolution.
//
// The precedence between four sources that can all name the same person.
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class DisplayNameResolutionTests
    {
        private const string Ana = "5511988887777@s.whatsapp.net";
        private const string Group = "120363000000000000@g.us";

        private static DisplayNameSources Sources(
            string? person = null,
            string? phone = null,
            string? whatsApp = null,
            string? jid = Ana,
            bool isGroup = false,
            bool isSender = false,
            PersonSource personSource = PersonSource.Observed) =>
            new DisplayNameSources
            {
                PersonName = person,
                PersonSource = personSource,
                PhoneContactName = phone,
                WhatsAppName = whatsApp,
                CanonicalJid = jid,
                IsGroup = isGroup,
                IsSenderContext = isSender
            };

        // --- Precedence --------------------------------------------------------

        [Fact]
        public void A_stored_name_that_came_from_the_address_book_is_used_first()
        {
            string name = DisplayNameResolution.Resolve(
                Sources(person: "Ana Paula", personSource: PersonSource.AddressBook, phone: "Ana Trabalho", whatsApp: "aninha"));

            Assert.Equal("Ana Paula", name);
        }

        [Fact]
        public void A_stored_name_of_unknown_origin_ranks_as_learned_not_saved()
        {
            // Rows from before the origin was recorded must not hide the address book.
            string name = DisplayNameResolution.Resolve(
                Sources(person: "aninha", personSource: PersonSource.Unknown, phone: "Ana Trabalho"));

            Assert.Equal("Ana Trabalho", name);
        }

        [Fact]
        public void The_address_book_beats_a_name_the_app_only_observed()
        {
            // A learned push name, or the number itself, must not hide the user's saved contact.
            string name = DisplayNameResolution.Resolve(
                Sources(person: "aninha", phone: "Ana Trabalho", whatsApp: "aninha"));

            Assert.Equal("Ana Trabalho", name);
        }

        [Fact]
        public void An_observed_name_still_beats_the_live_push_name()
        {
            string name = DisplayNameResolution.Resolve(Sources(person: "Ana Paula", whatsApp: "aninha"));

            Assert.Equal("Ana Paula", name);
        }

        [Fact]
        public void The_users_own_address_book_beats_the_name_the_contact_chose()
        {
            string name = DisplayNameResolution.Resolve(Sources(phone: "Ana Trabalho", whatsApp: "aninha"));

            Assert.Equal("Ana Trabalho", name);
        }

        [Fact]
        public void The_contacts_own_name_is_used_when_nothing_better_exists()
        {
            Assert.Equal("~aninha", DisplayNameResolution.Resolve(Sources(whatsApp: "aninha")));
        }

        [Fact]
        public void With_nothing_known_the_number_is_shown()
        {
            Assert.Equal("5511988887777", DisplayNameResolution.Resolve(Sources()));
        }

        [Fact]
        public void A_blank_source_is_skipped_rather_than_shown()
        {
            // Otherwise a whitespace-only entry would blank the row while better names exist.
            Assert.Equal("Ana Trabalho", DisplayNameResolution.Resolve(Sources(person: "   ", phone: "Ana Trabalho")));
        }

        // --- The tilde ---------------------------------------------------------

        [Fact]
        public void A_name_the_contact_chose_is_marked_as_unsaved()
        {
            Assert.Equal("~aninha", DisplayNameResolution.Resolve(Sources(whatsApp: "aninha")));
        }

        [Fact]
        public void A_name_that_already_carries_the_mark_is_not_marked_twice()
        {
            Assert.Equal("~aninha", DisplayNameResolution.Resolve(Sources(whatsApp: "~aninha")));
        }

        [Fact]
        public void Group_names_are_never_marked()
        {
            // A group's subject is not somebody's unsaved push name.
            Assert.Equal("Futebol", DisplayNameResolution.Resolve(
                Sources(whatsApp: "Futebol", jid: Group, isGroup: true)));
        }

        [Fact]
        public void Message_authors_are_never_marked()
        {
            // The tilde belongs in the chat list, not repeated above every bubble.
            Assert.Equal("aninha", DisplayNameResolution.Resolve(Sources(whatsApp: "aninha", isSender: true)));
        }

        [Fact]
        public void Only_the_contacts_own_name_gets_marked()
        {
            // A saved name is saved; marking it would claim otherwise.
            Assert.Equal("Ana Trabalho", DisplayNameResolution.Resolve(Sources(phone: "Ana Trabalho")));
            Assert.Equal("Ana Paula", DisplayNameResolution.Resolve(Sources(person: "Ana Paula")));
        }

        [Fact]
        public void Surrounding_whitespace_is_dropped_before_marking()
        {
            Assert.Equal("~aninha", DisplayNameResolution.Resolve(Sources(whatsApp: "  aninha  ")));
        }

        // --- The fallback ------------------------------------------------------

        [Fact]
        public void A_group_with_no_subject_falls_back_to_its_id()
        {
            Assert.Equal("120363000000000000", DisplayNameResolution.Resolve(
                Sources(jid: Group, isGroup: true)));
        }

        [Fact]
        public void An_address_with_no_domain_is_shown_whole()
        {
            Assert.Equal("5511988887777", DisplayNameResolution.Resolve(Sources(jid: "5511988887777")));
        }

        [Fact]
        public void Nothing_at_all_resolves_to_nothing_rather_than_throwing()
        {
            Assert.Equal(string.Empty, DisplayNameResolution.Resolve(Sources(jid: null)));
            Assert.Equal(string.Empty, DisplayNameResolution.Resolve(Sources(jid: "")));
        }
    }
}
