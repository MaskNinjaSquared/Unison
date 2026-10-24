// =============================================================================
// SchedulePostReplayMaintenanceDispatch
//
// Body of SchedulePostReplayMaintenance after CTS swap. Task.Run stays on host.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class SchedulePostReplayMaintenanceDispatch
    {
        public static void Run(ISchedulePostReplayMaintenanceHost host, int offlineCount)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.CancelPreviousPostReplayMaintenanceCts();

            CancellationTokenSource cts = host.ReplacePostReplayMaintenanceCts();
            CancellationToken token = cts.Token;

            host.FireAndForgetPostReplayMaintenance(async () =>
            {
                try
                {
                    await host.RunPostReplayMaintenanceAsync(offlineCount, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    host.ClearPostReplayMaintenanceCtsIfCurrent(cts);
                    cts.Dispose();
                }
            });
        }
    }
}
