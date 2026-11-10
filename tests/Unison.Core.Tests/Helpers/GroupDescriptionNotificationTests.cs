// =============================================================================
// GroupNotificationParser — the description case, and the updates that must not
// look like one.
//
// SocketBridge forwards a group update only when Description is non-null, so the
// difference between null and "" here is what stops an avatar change from blanking
// a stored description.
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Socket.Notifications;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupDescriptionNotificationTests
    {
        [Fact]
        public void A_description_change_carries_the_new_body_and_its_id()
        {
            GroupNotificationResult result = GroupNotificationParser.Parse(Notification(
                new BinaryNode(
                    "description",
                    new Dictionary<string, string> { ["id"] = "desc-7" },
                    new List<BinaryNode>
                    {
                        new BinaryNode("body", null, System.Text.Encoding.UTF8.GetBytes("Trip planning"))
                    })));

            Assert.NotNull(result.Update);
            Assert.Equal("Trip planning", result.Update.Description);
            Assert.Equal("desc-7", result.Update.DescriptionId);
        }

        [Fact]
        public void A_cleared_description_arrives_as_empty_rather_than_null()
        {
            GroupNotificationResult result = GroupNotificationParser.Parse(Notification(
                new BinaryNode("description", new Dictionary<string, string> { ["id"] = "desc-8" })));

            Assert.NotNull(result.Update);
            Assert.Equal(string.Empty, result.Update.Description);
        }

        [Fact]
        public void A_rename_says_nothing_about_the_description()
        {
            // Null is the signal the bridge filters on. A subject change that reported ""
            // would wipe the description every time somebody renamed the group.
            GroupNotificationResult result = GroupNotificationParser.Parse(Notification(
                new BinaryNode(
                    "subject",
                    new Dictionary<string, string> { ["subject"] = "New name" })));

            Assert.NotNull(result.Update);
            Assert.Null(result.Update.Description);
        }

        [Fact]
        public void An_announcement_toggle_says_nothing_about_the_description()
        {
            GroupNotificationResult result = GroupNotificationParser.Parse(
                Notification(new BinaryNode("announcement")));

            Assert.NotNull(result.Update);
            Assert.Null(result.Update.Description);
        }

        private static BinaryNode Notification(BinaryNode child)
        {
            return new BinaryNode(
                "notification",
                new Dictionary<string, string>
                {
                    ["from"] = "120363@g.us",
                    ["participant"] = "15551234567@s.whatsapp.net",
                    ["id"] = "n1"
                },
                new List<BinaryNode> { child });
        }
    }
}
