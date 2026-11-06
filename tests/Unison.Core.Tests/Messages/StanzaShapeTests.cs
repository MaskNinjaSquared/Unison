// =============================================================================
// StanzaShape — one-line outline of an outgoing message for a server refusal.
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Socket.Messages;
using Xunit;

namespace Unison.Core.Tests.Messages
{
    public sealed class StanzaShapeTests
    {
        [Fact]
        public void Outline_counts_targets_by_address_space_and_flags_repeats()
        {
            var stanza = new BinaryNode(
                "message",
                new Dictionary<string, string> { { "id", "3EB0" }, { "to", "status@broadcast" }, { "type", "text" } },
                new List<BinaryNode>
                {
                    new BinaryNode("participants", null, new List<BinaryNode>
                    {
                        To("1@lid", "pkmsg", 10),
                        To("1@lid", "msg", 10),
                        To("2:3@lid", "msg", 10),
                        To("5511@s.whatsapp.net", "msg", 10)
                    }),
                    new BinaryNode("enc", new Dictionary<string, string> { { "v", "2" }, { "type", "skmsg" } }, new byte[7]),
                    new BinaryNode("device-identity", null, new byte[3])
                });

            string outline = StanzaShape.Describe(stanza);

            Assert.Contains("to=status@broadcast", outline);
            Assert.Contains("type=text", outline);
            Assert.Contains("children=participants,enc,device-identity", outline);
            Assert.Contains("targets=4", outline);
            Assert.Contains("lid=3", outline);
            Assert.Contains("pn=1", outline);
            Assert.Contains("pkmsg=1", outline);
            Assert.Contains("repeated=1@lid", outline);
            Assert.Contains("bytes=50", outline);
        }

        [Fact]
        public void Outline_without_participants_still_names_the_children()
        {
            var stanza = new BinaryNode(
                "message",
                new Dictionary<string, string> { { "to", "status@broadcast" } },
                new List<BinaryNode> { new BinaryNode("enc", null, new byte[2]) });

            string outline = StanzaShape.Describe(stanza);

            Assert.Contains("children=enc", outline);
            Assert.Contains("targets=0", outline);
            Assert.DoesNotContain("repeated=", outline);
        }

        private static BinaryNode To(string jid, string type, int size) =>
            new BinaryNode(
                "to",
                new Dictionary<string, string> { { "jid", jid } },
                new List<BinaryNode>
                {
                    new BinaryNode("enc", new Dictionary<string, string> { { "v", "2" }, { "type", type } }, new byte[size])
                });
    }
}
