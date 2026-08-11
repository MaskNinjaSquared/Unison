using System.Collections.Generic;

namespace Unison.Baileys.Client
{
    public sealed class AppStateCollectionState
    {
        public string Name { get; set; }
        public long Version { get; set; }
        public byte[] Hash { get; set; } = new byte[128];
        public Dictionary<string, byte[]> IndexValueMap { get; set; } = new Dictionary<string, byte[]>();
    }
}
