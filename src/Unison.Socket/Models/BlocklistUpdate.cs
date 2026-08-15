// =============================================================================
// BlocklistUpdate
//
// Who was just blocked or unblocked. The full list arrives separately, on login,
// as a blocklist.set; this is only the delta.
//
// Ports: rc14 blocklist.update in src/Types/Events.ts
// =============================================================================
using System.Collections.Generic;

namespace Unison.Socket.Models
{
    public enum BlocklistAction
    {
        Add = 0,
        Remove = 1
    }

    public sealed class BlocklistUpdate
    {
        public BlocklistUpdate()
        {
            Jids = new List<string>();
        }

        public IList<string> Jids { get; private set; }

        public BlocklistAction Action { get; set; }
    }
}
