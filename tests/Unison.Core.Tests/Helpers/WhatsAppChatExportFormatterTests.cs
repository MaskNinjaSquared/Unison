using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class WhatsAppChatExportFormatterTests
    {
        [Fact]
        public void FormatMessageLine_hides_media_without_include()
        {
            var row = new HistoryMessage
            {
                TimestampUtc = new DateTime(2024, 1, 2, 15, 30, 0, DateTimeKind.Utc),
                Kind = ChatPreviewKind.Image,
                Body = "ignored"
            };

            string line = WhatsAppChatExportFormatter.FormatMessageLine(
                row,
                "Alice",
                includeMedia: false,
                out string vcf);

            Assert.Null(vcf);
            Assert.Contains("<Mídia oculta>", line);
            Assert.Contains("Alice:", line);
        }

        [Fact]
        public void FormatMessageLine_contact_emits_vcf_attachment_and_card()
        {
            var row = new HistoryMessage
            {
                TimestampUtc = new DateTime(2024, 7, 24, 12, 0, 0, DateTimeKind.Utc),
                Kind = ChatPreviewKind.Contact,
                MediaFileName = "Camila A. B.",
                MediaUrl = "554195193883@s.whatsapp.net",
                MediaDirectPath = "554195193883"
            };

            string line = WhatsAppChatExportFormatter.FormatMessageLine(
                row,
                "Gabriel",
                includeMedia: false,
                out string vcfName);

            Assert.Equal("Camila A. B..vcf", vcfName);
            Assert.Contains("(arquivo anexado)", line);

            string vcf = WhatsAppChatExportFormatter.BuildVcf(row);
            Assert.Contains("BEGIN:VCARD", vcf);
            Assert.Contains("FN:Camila A. B.", vcf);
            Assert.Contains("waid=554195193883", vcf);
        }

        [Fact]
        public void BuildBaseFileName_sanitizes_illegal_chars()
        {
            string name = WhatsAppChatExportFormatter.BuildBaseFileName("A:B/C?");
            Assert.Equal("Conversa do WhatsApp com A_B_C_", name);
        }
    }
}
