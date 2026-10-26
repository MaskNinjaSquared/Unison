// =============================================================================
// Tests for GroupJoinParticipantPrep + GroupJoinDisplayLabel.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers.Groups;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupJoinParticipantPrepTests
    {
        [Fact]
        public void Null_input_returns_empty()
        {
            GroupJoinParticipantPrepResult result =
                GroupJoinParticipantPrep.FromParticipantPhones(null);
            Assert.Empty(result.LidToPn);
            Assert.Empty(result.UsyncLookupJids);
        }

        [Fact]
        public void Lid_with_phone_builds_alias_and_usync_pn()
        {
            var rows = new[]
            {
                new KeyValuePair<string, string>("19282309298@lid", "5511999887766")
            };

            GroupJoinParticipantPrepResult result =
                GroupJoinParticipantPrep.FromParticipantPhones(rows);

            Assert.Single(result.LidToPn);
            Assert.Equal("19282309298@lid", result.LidToPn[0].Key);
            Assert.Equal("5511999887766@s.whatsapp.net", result.LidToPn[0].Value);
            Assert.Single(result.UsyncLookupJids);
            Assert.Equal("5511999887766@s.whatsapp.net", result.UsyncLookupJids[0]);
        }

        [Fact]
        public void Phone_jid_attribute_normalizes_to_digits_pn()
        {
            var rows = new[]
            {
                new KeyValuePair<string, string>(
                    "99@lid",
                    "5511888777666@s.whatsapp.net")
            };

            GroupJoinParticipantPrepResult result =
                GroupJoinParticipantPrep.FromParticipantPhones(rows);

            Assert.Equal("5511888777666@s.whatsapp.net", result.LidToPn[0].Value);
            Assert.Equal("5511888777666@s.whatsapp.net", result.UsyncLookupJids[0]);
        }

        [Fact]
        public void Lid_without_phone_skips_usync()
        {
            var rows = new[]
            {
                new KeyValuePair<string, string>("99@lid", null)
            };

            GroupJoinParticipantPrepResult result =
                GroupJoinParticipantPrep.FromParticipantPhones(rows);

            Assert.Empty(result.LidToPn);
            Assert.Empty(result.UsyncLookupJids);
        }

        [Fact]
        public void Direct_pn_without_phone_still_usyncs()
        {
            var rows = new[]
            {
                new KeyValuePair<string, string>("5511999@s.whatsapp.net", null)
            };

            GroupJoinParticipantPrepResult result =
                GroupJoinParticipantPrep.FromParticipantPhones(rows);

            Assert.Empty(result.LidToPn);
            Assert.Single(result.UsyncLookupJids);
            Assert.Equal("5511999@s.whatsapp.net", result.UsyncLookupJids[0]);
        }
    }

    public class GroupJoinDisplayLabelTests
    {
        [Fact]
        public void Prefers_resolved_name_over_phone()
        {
            Assert.Equal(
                "Ana",
                GroupJoinDisplayLabel.Pick("Ana", "5511999", "99@lid"));
        }

        [Fact]
        public void Falls_back_to_phone_when_label_is_lid()
        {
            Assert.Equal(
                "5511999",
                GroupJoinDisplayLabel.Pick("99@lid", "5511999", "99@lid"));
        }

        [Fact]
        public void NeedsBetterName_detects_at_jid()
        {
            Assert.True(GroupJoinDisplayLabel.NeedsBetterName("192@lid"));
            Assert.False(GroupJoinDisplayLabel.NeedsBetterName("Ana"));
        }
    }
}
