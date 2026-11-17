using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;
using Unison.Voip.Null;
using Xunit;

namespace Unison.Core.Tests.Voip
{
    public sealed class LoopbackUdpTransportTests
    {
        [Fact]
        public async Task Send_then_receive_round_trips_payload()
        {
            using (var udp = new LoopbackUdpTransport())
            {
                await udp.BindAsync(CancellationToken.None);

                var remote = new IPEndPoint(IPAddress.Loopback, 3478);
                byte[] payload = { 0x21, 0x12, 0xA4, 0x42 };

                await udp.SendAsync(payload, remote, CancellationToken.None);

                CallUdpDatagram got = await udp.ReceiveAsync(CancellationToken.None);

                Assert.Equal(1, udp.SentCount);
                Assert.NotNull(got);
                Assert.Equal(payload, got.Payload);
                Assert.Equal(remote, got.Remote);
                Assert.Equal(remote, udp.LastSent.Remote);
            }
        }

        [Fact]
        public async Task Receive_after_dispose_returns_null()
        {
            var udp = new LoopbackUdpTransport();
            await udp.BindAsync(CancellationToken.None);
            Task<CallUdpDatagram> pending = udp.ReceiveAsync(CancellationToken.None);

            udp.Dispose();

            Assert.Null(await pending);
            Assert.Null(await udp.ReceiveAsync(CancellationToken.None));
        }

        [Fact]
        public async Task Send_before_bind_throws()
        {
            using (var udp = new LoopbackUdpTransport())
            {
                await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
                    udp.SendAsync(
                        new byte[] { 1 },
                        new IPEndPoint(IPAddress.Loopback, 1),
                        CancellationToken.None));
            }
        }
    }
}
