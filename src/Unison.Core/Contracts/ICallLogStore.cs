// =============================================================================
// ICallLogStore
//
// Disk persistence for call Recents history (SQLite on the UWP host).
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    public interface ICallLogStore
    {
        Task InitializeAsync();

        Task<IReadOnlyList<CallHistoryEntry>> LoadAllAsync();

        Task UpsertAsync(CallHistoryEntry entry);
    }
}
