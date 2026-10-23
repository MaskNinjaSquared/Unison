using System.Collections.Generic;
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

        [Fact]
        public void Indexer_set_remembers_pair_into_memory()
        {
            var memory = new FakeMemory();
            var table = new JidAliasTable();
            table.AttachLidMappingMemory(memory);

            table["199999999999@lid"] = "5511999990000@s.whatsapp.net";

            Assert.Equal("199999999999@lid", memory.RememberedA);
            Assert.Equal("5511999990000@s.whatsapp.net", memory.RememberedB);
            Assert.Equal(1, memory.RememberPairCalls);
            Assert.Equal(0, memory.RememberPairsCalls);
        }

        [Fact]
        public void BeginBulkImport_coalesces_remember_into_one_pairs_call()
        {
            var memory = new FakeMemory();
            var table = new JidAliasTable();
            table.AttachLidMappingMemory(memory);
            int changed = 0;
            table.Changed += (_, __) => changed++;

            using (table.BeginBulkImport())
            {
                table["199999999999@lid"] = "5511999990000@s.whatsapp.net";
                table["288888888888@lid"] = "5511888880000@s.whatsapp.net";
                Assert.Equal(0, memory.RememberPairCalls);
                Assert.Equal(0, memory.RememberPairsCalls);
                Assert.Equal(0, changed);
            }

            Assert.Equal(0, memory.RememberPairCalls);
            Assert.Equal(1, memory.RememberPairsCalls);
            Assert.Equal(2, memory.LastPairs.Count);
            Assert.Equal(1, changed);
        }

        private sealed class FakeMemory : ILidMappingMemory
        {
            public string Paired { get; set; }
            public string RememberedA { get; private set; }
            public string RememberedB { get; private set; }
            public int RememberPairCalls { get; private set; }
            public int RememberPairsCalls { get; private set; }
            public List<KeyValuePair<string, string>> LastPairs { get; } =
                new List<KeyValuePair<string, string>>();

            public bool TryGetPairedJid(string normalizedJid, out string pairedJid)
            {
                pairedJid = Paired;
                return !string.IsNullOrEmpty(Paired);
            }

            public void RememberPair(string jidA, string jidB)
            {
                RememberPairCalls++;
                RememberedA = jidA;
                RememberedB = jidB;
            }

            public void RememberPairs(IEnumerable<KeyValuePair<string, string>> pairs)
            {
                RememberPairsCalls++;
                LastPairs.Clear();
                if (pairs == null)
                {
                    return;
                }

                LastPairs.AddRange(pairs);
            }
        }
    }
}
