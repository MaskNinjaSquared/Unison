using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class UsyncAliasHostTests
    {
        [Fact]
        public void Files_pair_then_registers_and_heals_on_the_socket_port()
        {
            var table = new JidAliasTable();
            var filing = new UsyncAliasFiling(table);
            var socket = new FakeRegistrar();
            var host = new UsyncAliasHost(filing, socket);

            bool filed = host.TryFileUsyncAlias(
                "5511999990001@s.whatsapp.net",
                "123456789012345@lid",
                out bool wasAlready);

            Assert.True(filed);
            Assert.False(wasAlready);
            Assert.Equal("5511999990001@s.whatsapp.net", socket.LastPn);
            Assert.Equal("123456789012345@lid", socket.LastLid);
            Assert.Equal("contact-usync", socket.LastSource);
            Assert.Equal(1, socket.Heals);
        }

        [Fact]
        public void Does_not_touch_the_socket_when_filing_rejects()
        {
            var table = new JidAliasTable();
            var filing = new UsyncAliasFiling(table);
            var socket = new FakeRegistrar();
            var host = new UsyncAliasHost(filing, socket);

            bool filed = host.TryFileUsyncAlias(null, "123@lid", out _);

            Assert.False(filed);
            Assert.Null(socket.LastPn);
            Assert.Equal(0, socket.Heals);
        }

        private sealed class FakeRegistrar : ISocketAliasRegistrar
        {
            public string LastPn { get; private set; }
            public string LastLid { get; private set; }
            public string LastSource { get; private set; }
            public int Heals { get; private set; }

            public void RegisterPair(string jidA, string jidB, string source)
            {
                LastPn = jidA;
                LastLid = jidB;
                LastSource = source;
            }

            public void HealOwnIdentityAfterUsync(string normalizedPn, string normalizedLid)
            {
                Heals++;
            }
        }
    }
}
