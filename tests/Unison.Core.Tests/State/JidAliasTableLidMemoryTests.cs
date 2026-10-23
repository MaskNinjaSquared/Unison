using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class JidAliasTableLidMemoryTests
    {
        [Fact]
        public void TryGetValue_promotes_pair_from_lid_memory()
        {
            var table = new JidAliasTable();
            table.AttachLidMappingMemory(new FakeMemory
            {
                Paired = "5511999990000@s.whatsapp.net"
            });

            Assert.True(table.TryGetValue("199999999999@lid", out string paired));
            Assert.Equal("5511999990000@s.whatsapp.net", paired);
            // Promoted into the table.
            Assert.True(table.ContainsKey("199999999999@lid"));
        }

        [Fact]
        public void GetCanonicalJid_uses_memory_when_table_misses()
        {
            var table = new JidAliasTable();
            table.AttachLidMappingMemory(new FakeMemory
            {
                Paired = "5511999990000@s.whatsapp.net"
            });

            Assert.Equal(
                "5511999990000@s.whatsapp.net",
                table.GetCanonicalJid("199999999999@lid"));
        }

        private sealed class FakeMemory : ILidMappingMemory
        {
            public string Paired { get; set; }

            public bool TryGetPairedJid(string normalizedJid, out string pairedJid)
            {
                pairedJid = Paired;
                return !string.IsNullOrEmpty(Paired);
            }
        }
    }
}
