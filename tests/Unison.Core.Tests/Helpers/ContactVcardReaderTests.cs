using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ContactVcardReaderTests
    {
        [Fact]
        public void Reads_waid_phone_and_fn_from_whatsapp_vcard()
        {
            const string vcard =
                "BEGIN:VCARD\n" +
                "VERSION:3.0\n" +
                "N:;Bob;;;\n" +
                "FN:Bob\n" +
                "TEL;type=CELL;waid=5511888777666:+55 11 88877-7666\n" +
                "END:VCARD";

            ContactVcardInfo info = ContactVcardReader.Read(null, vcard);

            Assert.Equal("Bob", info.DisplayName);
            Assert.Equal("5511888777666", info.Waid);
            Assert.Equal("5511888777666", info.PhoneDigits);
            Assert.Equal("5511888777666@s.whatsapp.net", info.Jid);
        }

        [Fact]
        public void Prefers_protocol_display_name_over_fn()
        {
            ContactVcardInfo info = ContactVcardReader.Read(
                "Protocol Name",
                "BEGIN:VCARD\nFN:Vcard Name\nEND:VCARD");

            Assert.Equal("Protocol Name", info.DisplayName);
        }

        [Fact]
        public void Reads_base64_photo_when_present()
        {
            ContactVcardInfo info = ContactVcardReader.Read(
                "Pic",
                "BEGIN:VCARD\nFN:Pic\nPHOTO;ENCODING=BASE64;TYPE=JPEG:/9j/4AAQSkZJRg==\nEND:VCARD");

            Assert.Equal("/9j/4AAQSkZJRg==", info.PhotoBase64);
            Assert.Equal("image/jpeg", info.PhotoMimeType);
        }
    }
}
