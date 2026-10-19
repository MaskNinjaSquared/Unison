// =============================================================================
// Group send signer / addressing — the seam that decides which of our identities
// encrypts a group sender key (Baileys transferDevice + addressing_mode).
// =============================================================================
using System.Linq;
using Unison.Socket.Messages;
using Unison.Socket.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupSendIdentityTests
    {
        private const string MePn = "5511999999999:55@s.whatsapp.net";
        private const string MeLidBare = "123456789012345@lid";
        private const string MeLidWithDevice = "123456789012345:55@lid";

        [Fact]
        public void Lid_group_signer_carries_the_companion_device_from_meId()
        {
            string signer = GroupSendIdentity.ResolveSigner("lid", MePn, MeLidBare);

            Assert.Equal(MeLidWithDevice, signer);
        }

        [Fact]
        public void Lid_group_signer_keeps_an_already_device_scoped_lid()
        {
            string signer = GroupSendIdentity.ResolveSigner("lid", MePn, MeLidWithDevice);

            Assert.Equal(MeLidWithDevice, signer);
        }

        [Fact]
        public void Pn_group_signer_is_our_phone_identity()
        {
            string signer = GroupSendIdentity.ResolveSigner("pn", MePn, MeLidBare);

            Assert.Equal(MePn, signer);
        }

        [Fact]
        public void Unset_mode_with_lid_participants_is_lid()
        {
            var metadata = new GroupMetadata
            {
                AddressingMode = GroupAddressingMode.Unset
            };
            metadata.Participants.Add(new GroupParticipant { Id = "999888777666555@lid" });

            string mode = GroupSendIdentity.ResolveAddressingMode(metadata, MeLidBare, out string source);

            Assert.Equal("lid", mode);
            Assert.Equal("inferred-lid-participants", source);
        }

        [Fact]
        public void Server_pn_wins_even_when_participants_look_like_lid()
        {
            var metadata = new GroupMetadata
            {
                AddressingMode = GroupAddressingMode.Pn
            };
            metadata.Participants.Add(new GroupParticipant { Id = "999888777666555@lid" });

            string mode = GroupSendIdentity.ResolveAddressingMode(metadata, MeLidBare, out string source);

            Assert.Equal("pn", mode);
            Assert.Equal("server", source);
        }

        [Fact]
        public void TransferDevice_copies_companion_device_onto_a_bare_lid()
        {
            Assert.Equal(
                MeLidWithDevice,
                Unison.Socket.WABinary.JidUtils.TransferDevice(MePn, MeLidBare));
        }

        [Fact]
        public void Own_other_primary_jids_are_device_zero_in_both_address_spaces()
        {
            var primaries = GroupSendIdentity.OwnOtherPrimaryJids(MePn, MeLidBare).ToList();

            Assert.Contains("5511999999999@s.whatsapp.net", primaries);
            Assert.Contains("123456789012345@lid", primaries);
            Assert.DoesNotContain(MePn, primaries);
        }

        [Fact]
        public void Own_other_primary_jids_still_emit_phone_when_meId_is_bare()
        {
            var primaries = GroupSendIdentity
                .OwnOtherPrimaryJids("5511999999999@s.whatsapp.net", MeLidWithDevice)
                .ToList();

            Assert.Contains("5511999999999@s.whatsapp.net", primaries);
            Assert.Contains("123456789012345@lid", primaries);
        }

        [Fact]
        public void Own_other_primary_jids_follow_group_addressing_mode()
        {
            var lidOnly = GroupSendIdentity.OwnOtherPrimaryJids(MePn, MeLidBare, "lid").ToList();
            Assert.Contains("123456789012345@lid", lidOnly);
            Assert.DoesNotContain("5511999999999@s.whatsapp.net", lidOnly);

            var pnOnly = GroupSendIdentity.OwnOtherPrimaryJids(MePn, MeLidBare, "pn").ToList();
            Assert.Contains("5511999999999@s.whatsapp.net", pnOnly);
            Assert.DoesNotContain("123456789012345@lid", pnOnly);
        }

        [Fact]
        public void Companion_device_falls_back_to_meLid_when_meId_is_bare()
        {
            Assert.Equal(55, GroupSendIdentity.ResolveCompanionDevice(
                "5511999999999@s.whatsapp.net",
                MeLidWithDevice));
            Assert.Equal(0, GroupSendIdentity.ResolveCompanionDevice(
                "5511999999999@s.whatsapp.net",
                MeLidBare));
        }

        [Fact]
        public void Phone_primary_is_recognized_as_own_other_device()
        {
            Assert.True(
                GroupSendIdentity.IsOwnOtherPrimaryDevice(
                    "5511999999999@s.whatsapp.net",
                    MePn,
                    MeLidBare));
            Assert.False(
                GroupSendIdentity.IsOwnOtherPrimaryDevice(MePn, MePn, MeLidBare));
        }
    }
}
