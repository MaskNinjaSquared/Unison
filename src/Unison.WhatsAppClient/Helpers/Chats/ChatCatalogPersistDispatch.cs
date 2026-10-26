// =============================================================================
// ChatCatalogPersistDispatch
//
// SchedulePersist / EnableScheduledPersist orchestration. Timer and SQLite
// PersistDataAsync stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ChatCatalogPersistDispatch
    {
        public const int DebounceMilliseconds = 3000;

        public static void Schedule(IChatCatalogPersistHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (host.RequestPersist() == PersistScheduleAction.Deferred)
            {
                host.LogDeferredDuringWarmup();
                return;
            }

            host.ArmDebounceTimer();
        }

        public static async Task OnDebounceElapsedAsync(IChatCatalogPersistHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.TryBeginPersist())
            {
                return;
            }

            // Put the save back if it did not land — same contract as OfflineReplayFlush.
            if (!await host.RunFullPersistAsync().ConfigureAwait(false))
            {
                host.RestorePersistDebt();
            }
        }

        public static void EnableAfterStartup(IChatCatalogPersistHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            PersistEnableResult result = host.EnableAfterStartup();
            if (result == PersistEnableResult.AlreadyEnabled)
            {
                return;
            }

            host.LogSuppressionLifted(reason);

            if (result == PersistEnableResult.LiftedWithDeferredSave)
            {
                host.LogFlushingDeferred(reason);
                Schedule(host);
            }
        }
    }
}
