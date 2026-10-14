// =============================================================================
// Tests for ContactUpdateFiling.
//
// How one contact from an app-state snapshot, a history chunk or a participant
// list is read before anything is filed. Used to be inline in
// WhatsAppService.ApplyContactUpdatesAsync:
//
//   - the contact's own Id fills whichever half (LID or phone) the update left out;
//   - a pair is reported whenever both halves are known, named or not;
//   - the name is Name, else Notify (blank counts as missing), and none files nothing;
//   - a named contact is filed under its Id and under each other half that differs.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ContactUpdateFilingTests
    {
        private const string Lid = "111@lid";
        private const string Pn = "5511999990001@s.whatsapp.net";

        [Fact]
        public void PhoneIdFillsMissingPhone()
        {
            var reading = ContactUpdateFiling.Read(Pn, Lid, null, "Ana", null);

            Assert.Equal(Lid, reading.Lid);
            Assert.Equal(Pn, reading.Phone);
            Assert.True(reading.HasAliasPair);
        }

        [Fact]
        public void LidIdFillsMissingLid()
        {
            var reading = ContactUpdateFiling.Read(Lid, null, Pn, "Ana", null);

            Assert.Equal(Lid, reading.Lid);
            Assert.Equal(Pn, reading.Phone);
            Assert.True(reading.HasAliasPair);
        }

        [Fact]
        public void LidIdDoesNotBecomePhone()
        {
            var reading = ContactUpdateFiling.Read(Lid, null, null, "Ana", null);

            Assert.Equal(Lid, reading.Lid);
            Assert.Null(reading.Phone);
            Assert.False(reading.HasAliasPair);
        }

        [Fact]
        public void LidSuffixIsMatchedIgnoringCase()
        {
            var reading = ContactUpdateFiling.Read("111@LID", null, null, "Ana", null);

            Assert.Equal("111@LID", reading.Lid);
            Assert.Null(reading.Phone);
        }

        [Fact]
        public void ExplicitHalvesAreKeptOverId()
        {
            var reading = ContactUpdateFiling.Read("222@lid", Lid, Pn, "Ana", null);

            Assert.Equal(Lid, reading.Lid);
            Assert.Equal(Pn, reading.Phone);
        }

        [Fact]
        public void PairIsReportedEvenWithoutName()
        {
            var reading = ContactUpdateFiling.Read(Pn, Lid, null, null, null);

            Assert.True(reading.HasAliasPair);
            Assert.Null(reading.Name);
            Assert.Empty(reading.NameAddresses);
        }

        [Fact]
        public void NameWinsOverNotify()
        {
            var reading = ContactUpdateFiling.Read(Pn, null, null, "Ana", "ana push");

            Assert.Equal("Ana", reading.Name);
        }

        [Fact]
        public void EmptyNameFallsBackToNotify()
        {
            var reading = ContactUpdateFiling.Read(Pn, null, null, "", "ana push");

            Assert.Equal("ana push", reading.Name);
        }

        [Fact]
        public void WhitespaceNameFallsBackToNotify()
        {
            var reading = ContactUpdateFiling.Read(Pn, null, null, "  ", "ana push");

            Assert.Equal("ana push", reading.Name);
        }

        [Fact]
        public void BlankNameAndNotifyFileNothing()
        {
            var reading = ContactUpdateFiling.Read(Pn, null, null, "  ", " ");

            Assert.Null(reading.Name);
            Assert.Empty(reading.NameAddresses);
        }

        [Fact]
        public void PaddedLidIdIsStillALid()
        {
            var reading = ContactUpdateFiling.Read("111@lid ", null, null, "Ana", null);

            Assert.Equal("111@lid ", reading.Lid);
            Assert.Null(reading.Phone);
        }

        [Fact]
        public void NamedContactIsFiledUnderIdAndOtherHalf()
        {
            var reading = ContactUpdateFiling.Read(Pn, Lid, null, "Ana", null);

            Assert.Equal(new[] { Pn, Lid }, reading.NameAddresses);
        }

        [Fact]
        public void NamedContactIsFiledUnderAllThreeWhenDistinct()
        {
            var reading = ContactUpdateFiling.Read("222@lid", Lid, Pn, "Ana", null);

            Assert.Equal(new[] { "222@lid", Lid, Pn }, reading.NameAddresses);
        }

        [Fact]
        public void HalfEqualToIdIgnoringCaseIsNotFiledTwice()
        {
            var reading = ContactUpdateFiling.Read("111@LID", Lid, Pn, "Ana", null);

            Assert.Equal(new[] { "111@LID", Pn }, reading.NameAddresses);
        }

        [Fact]
        public void EmptyIdIsNotFiledButHalvesAre()
        {
            var reading = ContactUpdateFiling.Read(null, Lid, Pn, "Ana", null);

            Assert.True(reading.HasAliasPair);
            Assert.Equal(new[] { Lid, Pn }, reading.NameAddresses);
        }
    }
}
