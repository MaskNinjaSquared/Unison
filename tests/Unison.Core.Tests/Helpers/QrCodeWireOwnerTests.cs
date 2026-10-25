using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class QrCodeWireOwnerTests
    {
        [Fact]
        public void Stale_skips_raise()
        {
            string raised = null;
            var owner = new QrCodeWireOwner(
                () => { },
                _ => { },
                () => { },
                qr => raised = qr);

            owner.Handle("qr-data", isCurrentSocket: false);

            Assert.Null(raised);
        }

        [Fact]
        public void Current_marks_and_raises()
        {
            bool delivered = false;
            string raised = null;
            var owner = new QrCodeWireOwner(
                () => { },
                _ => { },
                () => delivered = true,
                qr => raised = qr);

            owner.Handle("qr-data", isCurrentSocket: true);

            Assert.True(delivered);
            Assert.Equal("qr-data", raised);
        }
    }
}
