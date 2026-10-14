// =============================================================================
// Tests for SelfNameCandidate.
//
// The account's own name is picked from several sources (address book, WhatsApp
// cache, credentials, sidebar). Each candidate is dropped when it is only the
// "(You)" marker, a masked phone, or the account's own number written back —
// otherwise that echo blocks a real push name from ever replacing it.
// =============================================================================
using System;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SelfNameCandidateTests
    {
        private const string MePn = "5511999990001@s.whatsapp.net";
        private const string MeLid = "123456789012345@lid";

        private static readonly ISelfMarkerNaming Markers = new KnownMarkers();

        private static string Normalize(string candidate) =>
            SelfNameCandidate.Normalize(candidate, MePn, MeLid, Markers);

        [Fact]
        public void A_real_name_is_kept_trimmed()
        {
            Assert.Equal("Ana", Normalize("  Ana "));
        }

        [Fact]
        public void The_marker_is_stripped_from_a_name()
        {
            Assert.Equal("Ana", Normalize("Ana (You)"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        [InlineData("(You)")]
        [InlineData("You")]
        public void Blank_or_marker_only_is_no_name(string candidate)
        {
            Assert.Null(Normalize(candidate));
        }

        [Fact]
        public void A_masked_phone_is_no_name()
        {
            Assert.Null(Normalize("+55 •••• ••01"));
        }

        [Theory]
        [InlineData("5511999990001")]
        [InlineData("123456789012345")]
        [InlineData(MePn)]
        [InlineData("5511999990001:3@s.whatsapp.net")]
        public void The_own_address_is_no_name(string candidate)
        {
            Assert.Null(Normalize(candidate));
        }

        [Theory]
        [InlineData("+55 11 99999-0001")]
        [InlineData("+5511999990001")]
        [InlineData("55 (11) 99999-0001")]
        public void The_own_number_formatted_is_no_name(string candidate)
        {
            // Compared as an exact string, these passed as a name on the self path while
            // ContactLabelSanitizer.IsPhoneEcho already caught them for contacts.
            Assert.Null(Normalize(candidate));
        }

        [Fact]
        public void Someone_elses_number_is_kept()
        {
            Assert.Equal("+55 11 98888-0002", Normalize("+55 11 98888-0002"));
        }

        [Fact]
        public void An_echo_is_the_own_number_but_not_the_marker()
        {
            Assert.True(SelfNameCandidate.IsOwnPhoneEcho("+55 11 99999-0001", MePn, MeLid, Markers));
            Assert.True(SelfNameCandidate.IsOwnPhoneEcho("5511999990001", MePn, MeLid, Markers));
            Assert.False(SelfNameCandidate.IsOwnPhoneEcho("(You)", MePn, MeLid, Markers));
            Assert.False(SelfNameCandidate.IsOwnPhoneEcho("Ana", MePn, MeLid, Markers));
            Assert.False(SelfNameCandidate.IsOwnPhoneEcho(" ", MePn, MeLid, Markers));
        }

        [Fact]
        public void No_account_means_no_echo()
        {
            Assert.False(SelfNameCandidate.IsOwnPhoneEcho("5511999990001", null, null, Markers));
        }

        private sealed class KnownMarkers : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) => SelfChatNaming.IsSelfMarkerLabel(label);

            public string StripMarker(string label) => SelfChatNaming.StripMarker(label);
        }
    }
}
