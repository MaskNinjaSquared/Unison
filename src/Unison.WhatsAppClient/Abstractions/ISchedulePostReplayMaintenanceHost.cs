// =============================================================================
// ISchedulePostReplayMaintenanceHost
//
// Side effects for SchedulePostReplayMaintenanceDispatch (CTS + Task.Run arm).
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ISchedulePostReplayMaintenanceHost
    {
        void CancelPreviousPostReplayMaintenanceCts();

        CancellationTokenSource ReplacePostReplayMaintenanceCts();

        void FireAndForgetPostReplayMaintenance(Func<Task> work);

        Task RunPostReplayMaintenanceAsync(int offlineCount, CancellationToken token);

        void ClearPostReplayMaintenanceCtsIfCurrent(CancellationTokenSource cts);
    }
}
