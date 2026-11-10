using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.WhatsAppClient.Messages;
using Unison.WhatsAppClient.Status;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class StatusViewReceiptTests
    {
        private const string Viewer = "5511888880000@s.whatsapp.net";
        private const string Self = "5511999990000@s.whatsapp.net";

        [Theory]
        [InlineData("read")]
        [InlineData("played")]
        public void Read_or_played_on_status_broadcast_from_someone_else_is_a_view(string type)
        {
            Assert.True(StatusViewReceipt.IsViewerReceipt(Facts(type, Viewer), IsSelf));
        }

        [Theory]
        [InlineData("read-self")]
        [InlineData("played-self")]
        [InlineData("")]
        [InlineData("sender")]
        public void Self_echoes_and_delivery_are_not_views(string type)
        {
            Assert.False(StatusViewReceipt.IsViewerReceipt(Facts(type, Viewer), IsSelf));
        }

        [Fact]
        public void Own_device_participant_is_not_a_view()
        {
            Assert.False(StatusViewReceipt.IsViewerReceipt(Facts("read", Self), IsSelf));
        }

        [Fact]
        public void Receipt_outside_status_broadcast_is_not_a_view()
        {
            ReceiptFacts facts = Facts("read", Viewer);
            facts.ChatJid = Viewer;

            Assert.False(StatusViewReceipt.IsViewerReceipt(facts, IsSelf));
        }

        private static bool IsSelf(string jid) =>
            string.Equals(jid, Self, StringComparison.OrdinalIgnoreCase);

        private static ReceiptFacts Facts(string type, string participant) => new ReceiptFacts
        {
            ReceiptType = type,
            Status = "read",
            ChatJid = JidHelper.StatusBroadcastJid,
            Participant = participant,
            MessageIds = new List<string> { "ABC" },
            TimestampUtc = new DateTime(2026, 11, 3, 17, 0, 0, DateTimeKind.Utc)
        };
    }
}
