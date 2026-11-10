// =============================================================================
// FirstSyncListReleaseDispatch
//
// Body of RequestFirstSyncListReleaseAfterNames after gate decide. CAS /
// Task.Run / name resolution stay on the host.
//
// List unlock is never blocked on directory names: IsCompleted publishes first,
// then a background pass resolves placeholders in workload-sized usync lots.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class FirstSyncListReleaseDispatch
    {
        public static void Schedule(
            IFirstSyncListReleaseHost host,
            string stage,
            int processed,
            int total)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            FirstSyncListReleaseAction action = FirstSyncListReleaseGates.Decide(
                HistoryFirstSyncListHoldDecision.ShouldScheduleNamesAfterListRelease(
                    host.IsFirstSyncListHoldInProgress),
                host.IsFirstSyncUnlockInFlight);

            if (action == FirstSyncListReleaseAction.CompleteNow)
            {
                host.PublishInitialSyncProgress(false, true, processed, total, stage);
                return;
            }

            if (action == FirstSyncListReleaseAction.SkipAlreadyInFlight ||
                !host.TryClaimFirstSyncUnlockInFlight())
            {
                host.LogFirstSyncListRelease(
                    "First-sync list unlock already in flight (" + stage + ")");
                return;
            }

            // Unlock first — VisibleChats may paint while names catch up.
            host.PublishInitialSyncProgress(false, true, processed, total, stage);
            host.ClearSqliteHistoryConversationsAccumulated();
            host.ScheduleDeferredAvatarsAfterUnlock(stage);
            host.ClearEnrichmentAwaitingHeavyHistory();
            host.TrySchedulePostMessageEnrichment(
                "after-first-sync-unlock:" + (stage ?? string.Empty));
            host.WriteFirstSyncNamesJournal(stage, processed);

            string stageCapture = stage;
            host.FireAndForgetFirstSyncUnlock(
                () => RunNamesAfterUnlockAsync(host, stageCapture));
        }

        public static async Task RunNamesAfterUnlockAsync(
            IFirstSyncListReleaseHost host,
            string stage)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                await host.ResolveNamesBeforeListReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.LogFirstSyncListRelease(
                    "First-sync names after list failed: " + ex.Message);
                host.RecordFirstSyncNamesException(ex, stage);
            }
            finally
            {
                host.ClearFirstSyncUnlockInFlight();
            }
        }

        /// <summary>Obsolete name — prefer <see cref="RunNamesAfterUnlockAsync"/>.</summary>
        public static Task RunNamesThenUnlockAsync(
            IFirstSyncListReleaseHost host,
            string stage,
            int processed)
        {
            return RunNamesAfterUnlockAsync(host, stage);
        }
    }
}
