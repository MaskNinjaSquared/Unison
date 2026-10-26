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
        public void Prefers_resolved_name_and_strips_tilde()
        {
            Assert.Equal(
                "Ana",
                NotificationDisplayLabel.Prefer("~Ana", "99@lid", "99@lid"));
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
    }
}
