// =============================================================================
// Tests for GroupJoinNotifyMap.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers.Groups;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupJoinNotifyMapTests
    {
        [Fact]
        public void Blank_group_or_null_participants_returns_empty()
        {
            Assert.Empty(GroupJoinNotifyMap.FromAddParticipants(
                null, new[] { "a@s.whatsapp.net" }, null, null, DateTime.UtcNow));
            Assert.Empty(GroupJoinNotifyMap.FromAddParticipants(
                "g@g.us", null, null, null, DateTime.UtcNow));
        }

        [Fact]
        public void Skips_blank_participants_and_maps_phones()
        {
            var phones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lid@lid"] = "5511999"
            };

            IReadOnlyList<GroupJoinNotifyRequest> rows = GroupJoinNotifyMap.FromAddParticipants(
                "120@g.us",
                new[] { "lid@lid", " ", null, "pn@s.whatsapp.net" },
                phones,
                "nid-1",
                new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            Assert.Equal(2, rows.Count);
            Assert.Equal("lid@lid", rows[0].ParticipantJid);
            Assert.Equal("5511999", rows[0].PhoneDigits);
            Assert.Equal("nid-1", rows[0].NotificationId);
            Assert.Equal("pn@s.whatsapp.net", rows[1].ParticipantJid);
            Assert.Null(rows[1].PhoneDigits);
        }

        [Fact]
        public void FromAdd_copies_actor_jid()
        {
            IReadOnlyList<GroupJoinNotifyRequest> rows = GroupJoinNotifyMap.FromAddParticipants(
                "120@g.us",
                new[] { "user@s.whatsapp.net" },
                null,
                "nid",
                DateTime.UtcNow,
                "admin@s.whatsapp.net");

            Assert.Equal(1, rows.Count);
            Assert.Equal("admin@s.whatsapp.net", rows[0].ActorJid);
        }

        [Fact]
        public void FromRemove_maps_leave_rows_with_actor()
        {
            var phones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lid@lid"] = "5511"
            };

            IReadOnlyList<GroupLeaveNotifyRequest> rows = GroupJoinNotifyMap.FromRemoveParticipants(
                "120@g.us",
                new[] { "lid@lid", " " },
                phones,
                "nid-r",
                new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
                "admin@s.whatsapp.net");

            Assert.Equal(1, rows.Count);
            Assert.Equal("lid@lid", rows[0].ParticipantJid);
            Assert.Equal("5511", rows[0].PhoneDigits);
            Assert.Equal("admin@s.whatsapp.net", rows[0].ActorJid);
            Assert.Equal("nid-r", rows[0].NotificationId);
        }
    }
}
