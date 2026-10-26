using System.Collections.Generic;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ResolvedNameBatchPrepTests
    {
        [Fact]
        public void A_blank_name_is_ignored()
        {
            var entries = new List<ResolvedNameEntry>();

            ResolvedNameBatchPrep.Add(
                entries,
                "1@s.whatsapp.net",
                "  ",
                isSubject: false,
                jid => jid,
                jid => jid,
                (name, jid) => name);

            Assert.Empty(entries);
        }

        [Fact]
        public void A_contact_name_is_sanitized_before_filing()
        {
            var entries = new List<ResolvedNameEntry>();

            ResolvedNameBatchPrep.Add(
                entries,
                "1@s.whatsapp.net",
                "Alice",
                isSubject: false,
                jid => jid,
                jid => "1@s.whatsapp.net",
                (name, jid) => "Sanitized-" + name);

            Assert.Single(entries);
            Assert.Equal("Sanitized-Alice", entries[0].Name);
            Assert.False(entries[0].IsSubject);
        }

        [Fact]
        public void A_subject_keeps_its_raw_name()
        {
            var entries = new List<ResolvedNameEntry>();

            ResolvedNameBatchPrep.Add(
                entries,
                "120@g.us",
                "Family",
                isSubject: true,
                jid => jid,
                jid => jid,
                (name, jid) => "should-not-run");

            Assert.Single(entries);
            Assert.Equal("Family", entries[0].Name);
            Assert.True(entries[0].IsSubject);
        }
    }
}
