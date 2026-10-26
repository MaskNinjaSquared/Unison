using System.Collections.Generic;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class QrCodeWireDispatchTests
    {
        [Fact]
        public void Stale_socket_logs_and_skips_raise()
        {
            var host = new FakeHost();

            QrCodeWireDispatch.Handle(host, "qr-data", isCurrentSocket: false);

            Assert.Equal(new[] { "stale" }, host.Log);
            Assert.Null(host.RaisedQr);
        }

        [Fact]
        public void Current_marks_delivered_and_raises()
        {
            var host = new FakeHost();

            QrCodeWireDispatch.Handle(host, "qr-data", isCurrentSocket: true);

            Assert.Equal(
                new[] { "raise-log:7", "delivered", "raised" },
                host.Log);
            Assert.Equal("qr-data", host.RaisedQr);
        }

        private sealed class FakeHost : IQrCodeWireHost
        {
            public List<string> Log { get; } = new List<string>();

            public string RaisedQr { get; private set; }

            public void LogStaleQrIgnored() => Log.Add("stale");

            public void LogQrRaising(int length) => Log.Add("raise-log:" + length);

            public void MarkQrDelivered() => Log.Add("delivered");

            public void RaiseQrCodeReceived(string qr)
            {
                RaisedQr = qr;
                Log.Add("raised");
            }
        }
    }
}
