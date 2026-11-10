// =============================================================================
// Server-only JIDs ("@g.us") — rc14 jidDecode accepts an empty user, so the
// encoder writes JID_PAIR(LIST_EMPTY, server). Sent as a raw string the server
// silently drops the IQ (group create / participating time out).
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class BinaryEncoderServerJidTests
    {
        private const byte JidPair = 250;
        private const byte ListEmpty = 0;

        [Fact]
        public void Group_server_is_written_as_a_jid_pair_with_an_empty_user()
        {
            var node = new BinaryNode("iq", new Dictionary<string, string> { { "to", "@g.us" } });

            byte[] bytes = new BinaryEncoder().Encode(node);

            byte gUsToken = WAConstants.TokenMap["g.us"];
            byte toToken = WAConstants.TokenMap["to"];
            int toAt = System.Array.IndexOf(bytes, toToken);
            Assert.True(toAt > 0);
            Assert.Equal(new byte[] { JidPair, ListEmpty, gUsToken }, Slice(bytes, toAt + 1, 3));
        }

        [Fact]
        public void Group_server_round_trips_through_the_decoder()
        {
            var node = new BinaryNode("iq", new Dictionary<string, string> { { "to", "@g.us" } });

            BinaryNode decoded = BinaryDecoder.Decode(new BinaryEncoder().Encode(node));

            Assert.Equal("@g.us", decoded.GetAttribute("to"));
        }

        private static byte[] Slice(byte[] source, int start, int length)
        {
            var slice = new byte[length];
            System.Array.Copy(source, start, slice, 0, length);
            return slice;
        }
    }
}
