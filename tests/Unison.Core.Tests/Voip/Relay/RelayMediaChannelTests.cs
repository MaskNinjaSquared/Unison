using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Engine.Relay;
using Xunit;

namespace Unison.Core.Tests.Voip.Relay
{
    public sealed class RelayMediaChannelTests
    {
        [Fact]
        public async Task Full_stack_client_and_server_exchange_binary_messages()
        {
            CrossLinkedUdpPair pair = CrossLinkedUdpPair.Create();
            var remote = new IPEndPoint(IPAddress.Loopback, 3478);

            Task<RelayMediaChannel> serverTask = RelayMediaChannel.AcceptAsync(
                pair.Server, remote, CancellationToken.None, ownsUdp: false);
            RelayMediaChannel client = await RelayMediaChannel.ConnectAsync(
                pair.Client, remote, CancellationToken.None, ownsUdp: false);
            RelayMediaChannel server = await serverTask;

            byte[] ping = Encoding.ASCII.GetBytes("allocate-or-ping");
            await client.SendAsync(ping, CancellationToken.None);
            byte[] got = await server.RecvAsync(CancellationToken.None);
            Assert.Equal(ping, got);

            client.Dispose();
            server.Dispose();
        }
    }
}
