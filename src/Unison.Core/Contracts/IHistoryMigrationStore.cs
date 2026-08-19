using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// SQLite gate for JSON→SQLite history migration. One row; Succeeded after a history batch lands.
    /// </summary>
    public interface IHistoryMigrationStore
    {
        Task InitializeAsync();

        Task<HistoryMigrationState> GetAsync();

        Task MarkInProgressAsync(string syncId, string syncType, string reason = null);

        Task MarkSucceededAsync(string syncId, string syncType, int conversationCount);

        Task MarkFailedAsync(string syncId, string error, string syncType = null);

        /// <summary>Resets to Pending (wipe / resync / new MessageStore epoch).</summary>
        Task ResetAsync(string reason = null);
    }
}
