// =============================================================================
// Tests for NotificationDisplayLabel.
// =============================================================================
using Unison.Core.Helpers.Notifications;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class NotificationDisplayLabelTests
    {
        [Fact]
        public void Prefers_resolved_name_and_keeps_unsaved_tilde_with_space()
        {
            Assert.Equal(
                "~ Ana",
                NotificationDisplayLabel.Prefer("~Ana", "99@lid", "99@lid"));
            Assert.Equal(
                "~ Ana",
                NotificationDisplayLabel.Prefer("~ Ana", "99@lid", "99@lid"));
        }

        [Fact]
        public void Keeps_usable_current_when_resolve_still_lid()
        {
            Assert.Equal(
                "Ana",
                NotificationDisplayLabel.Prefer("99@lid", "Ana", "99@lid"));
        }

        [Fact]
        public void Falls_back_to_phone_over_lid()
        {
            Assert.Equal(
                "5511999",
                NotificationDisplayLabel.Prefer("99@lid", "99@lid", "5511999@s.whatsapp.net"));
        }

        [Fact]
        public void Group_rejects_numeric_id_and_keeps_subject()
        {
            Assert.Equal(
                "Family",
                NotificationDisplayLabel.Prefer(
                    "120363000000000000",
                    "Family",
                    "120363000000000000@g.us"));
        }

        [Fact]
        public void Group_returns_empty_when_only_id_placeholders()
        {
            Assert.Equal(
                string.Empty,
                NotificationDisplayLabel.Prefer(
                    "120363000000000000",
                    "120363000000000000@g.us",
                    "120363000000000000@g.us"));
        }

        [Fact]
        public void Community_announce_toast_title_uses_localized_string_not_subject()
        {
            Assert.Equal(
                "Avisos",
                NotificationDisplayLabel.PreferChatTitle(
                    "Grupo de Avisos da Família",
                    "Grupo de Avisos da Família",
                    "120363000000000000@g.us",
                    isCommunityAnnounce: true,
                    communityAnnouncementsLabel: "Avisos"));
        }

        [Fact]
        public void Community_subgroup_toast_keeps_subject_over_stale_contact_label()
        {
            // Same policy as GroupNaming: subjects-first label must win when the call site
            // passes it as resolved (ContactDisplayNames used to feed Prefer and rename toasts).
            Assert.Equal(
                "Geral",
                NotificationDisplayLabel.PreferChatTitle(
                    "Geral",
                    "Comunidade Família",
                    "120363000000000000@g.us",
                    isCommunityAnnounce: false,
                    communityAnnouncementsLabel: null));
        }
    }
}
