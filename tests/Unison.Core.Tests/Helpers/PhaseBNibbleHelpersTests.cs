// =============================================================================
// Phase B MOVE_NOW helpers: quoted sender naming + person persist gate.
// =============================================================================
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class QuotedSenderNamingTests
    {
        [Fact]
        public void Blank_participant_is_null()
        {
            Assert.Null(QuotedSenderNaming.Resolve(
                null,
                _ => false,
                _ => "X",
                _ => "Y",
                "You"));
        }

        [Fact]
        public void Self_uses_localized_fallback()
        {
            Assert.Equal(
                "You",
                QuotedSenderNaming.Resolve(
                    "me@lid",
                    jid => jid == "me@lid",
                    _ => "Push",
                    _ => "Sender",
                    "You"));
        }

        [Fact]
        public void Jid_looking_quote_name_falls_back_to_sender_context()
        {
            Assert.Equal(
                "Ana",
                QuotedSenderNaming.Resolve(
                    "5511@s.whatsapp.net",
                    _ => false,
                    _ => "5511@s.whatsapp.net",
                    _ => "Ana",
                    "You"));
        }
    }

    public class PersonNamePersistGateTests
    {
        [Fact]
        public void Same_cached_name_skips_persist()
        {
            Assert.False(PersonNamePersistGate.ShouldPersist(
                new Person { Name = "Ana", Source = PersonSource.Observed },
                "Ana"));
        }

        [Fact]
        public void Address_book_source_skips_persist()
        {
            Assert.False(PersonNamePersistGate.ShouldPersist(
                new Person { Name = "Old", Source = PersonSource.AddressBook },
                "New"));
        }

        [Fact]
        public void New_push_name_persists()
        {
            Assert.True(PersonNamePersistGate.ShouldPersist(
                new Person { Name = "Old", Source = PersonSource.Observed },
                "New"));
            Assert.True(PersonNamePersistGate.ShouldPersist(null, "New"));
        }
    }

    public class FreshnessTimestampFormatTests
    {
        [Fact]
        public void MinValue_reads_as_none()
        {
            Assert.Equal("<none>", FreshnessTimestampFormat.ForLog(System.DateTime.MinValue));
        }
    }
}
