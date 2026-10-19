// =============================================================================
// Tests for NameSource and its rules.
//
// One ranking decides both which stored name may be overwritten and which name
// is shown. The write rule on Person is pinned here so it cannot drift from it.
// =============================================================================
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public class NameSourceTests
    {
        // --- Ranking -----------------------------------------------------------

        [Theory]
        [InlineData(NameSource.Number, NameSource.PushName)]
        [InlineData(NameSource.PushName, NameSource.Learned)]
        [InlineData(NameSource.Learned, NameSource.AddressBook)]
        public void A_more_trusted_name_replaces_a_less_trusted_one(NameSource current, NameSource candidate)
        {
            Assert.True(current.CanBeReplacedBy(candidate));
            Assert.False(candidate.CanBeReplacedBy(current));
        }

        [Theory]
        [InlineData(NameSource.Number)]
        [InlineData(NameSource.PushName)]
        [InlineData(NameSource.Learned)]
        [InlineData(NameSource.AddressBook)]
        public void A_name_of_the_same_rank_replaces_the_current_one(NameSource source)
        {
            // Lets a contact renamed in the address book overwrite the old saved name.
            Assert.True(source.CanBeReplacedBy(source));
        }

        [Fact]
        public void Only_the_name_the_contact_chose_is_marked_as_unsaved()
        {
            Assert.True(NameSource.PushName.IsChosenByContact());
            Assert.False(NameSource.AddressBook.IsChosenByContact());
            Assert.False(NameSource.Learned.IsChosenByContact());
            Assert.False(NameSource.Number.IsChosenByContact());
        }

        // --- Stored origin -----------------------------------------------------

        [Fact]
        public void Only_an_address_book_match_counts_as_saved_by_the_user()
        {
            Assert.Equal(NameSource.AddressBook, PersonSource.AddressBook.ToNameSource());
            Assert.Equal(NameSource.Learned, PersonSource.DirectChat.ToNameSource());
            Assert.Equal(NameSource.Learned, PersonSource.Observed.ToNameSource());
            Assert.Equal(NameSource.Learned, PersonSource.Unknown.ToNameSource());
        }

        [Theory]
        [InlineData(PersonSource.Unknown, PersonSource.Observed, true)]
        [InlineData(PersonSource.Observed, PersonSource.Observed, true)]
        [InlineData(PersonSource.DirectChat, PersonSource.Observed, true)]
        [InlineData(PersonSource.Observed, PersonSource.AddressBook, true)]
        [InlineData(PersonSource.AddressBook, PersonSource.AddressBook, true)]
        [InlineData(PersonSource.AddressBook, PersonSource.Observed, false)]
        [InlineData(PersonSource.AddressBook, PersonSource.DirectChat, false)]
        [InlineData(PersonSource.AddressBook, PersonSource.Unknown, false)]
        public void A_saved_name_is_never_overwritten_by_a_learned_one(
            PersonSource existing,
            PersonSource incoming,
            bool canWrite)
        {
            Assert.Equal(canWrite, Person.CanWriteName(existing, incoming));
        }
    }
}
