// =============================================================================
// WaPatchName
//
// The five collections the account's settings are split across.
//
// The split is the server's, not ours, and it is about delivery priority rather
// than meaning: blocking someone is critical and has to survive a bad
// connection, while starring a message can wait. That is why a single user
// action sometimes lands in a collection you would not expect.
//
// Ports: rc14 WAPatchName and ALL_WA_PATCH_NAMES in src/Types/Chat.ts
// =============================================================================
using System.Collections.Generic;

namespace Unison.Socket.AppState
{
    public static class WaPatchName
    {
        public const string CriticalBlock = "critical_block";
        public const string CriticalUnblockLow = "critical_unblock_low";
        public const string RegularHigh = "regular_high";
        public const string RegularLow = "regular_low";
        public const string Regular = "regular";

        /// <summary>All five, in the order the reference syncs them on login.</summary>
        public static readonly IList<string> All = new List<string>
        {
            CriticalBlock,
            CriticalUnblockLow,
            RegularHigh,
            RegularLow,
            Regular
        }.AsReadOnly();

        public static bool IsKnown(string name)
        {
            return All.Contains(name);
        }
    }
}
