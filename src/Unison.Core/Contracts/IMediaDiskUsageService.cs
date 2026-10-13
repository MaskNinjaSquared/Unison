using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Measures local chat media and deletes those files without removing conversations.
    /// </summary>
    public interface IMediaDiskUsageService
    {
        Task<MediaDiskUsage> MeasureAsync();

        /// <summary>
        /// Marks each file deleted in the store, then removes the files one by one.
        /// A later clean skips rows already marked.
        /// </summary>
        Task CleanAsync();
    }
}
