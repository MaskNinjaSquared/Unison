// =============================================================================
// FirstSyncListReleaseDispatch
//
// Body of RequestFirstSyncListReleaseAfterNames after gate decide. CAS /
// Task.Run / name resolution stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;
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
                HistoryFirstSyncListHoldDecision.ShouldResolveNamesBeforeListRelease(
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

            host.MarkFirstSyncListReleaseDeferred();
            host.PublishInitialSyncProgress(
                true,
                false,
                processed,
                Math.Max(processed, 1),
                stage + "-names");
            host.WriteFirstSyncNamesJournal(stage, processed);

            string stageCapture = stage;
            int processedCapture = processed;
            host.FireAndForgetFirstSyncUnlock(
                () => RunNamesThenUnlockAsync(host, stageCapture, processedCapture));
        }

        public static async Task RunNamesThenUnlockAsync(
            IFirstSyncListReleaseHost host,
            string stage,
            int processed)
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
                    "First-sync names before list failed: " + ex.Message);
                host.RecordFirstSyncNamesException(ex, stage);
            }
            finally
            {
                try
                {
                    host.PublishInitialSyncProgress(
                        false,
                        true,
                        processed,
                        Math.Max(processed, 1),
                        stage);
                    host.ClearSqliteHistoryConversationsAccumulated();
                    host.ScheduleDeferredAvatarsAfterUnlock(stage);
                    host.ClearEnrichmentAwaitingHeavyHistory();
                    host.TrySchedulePostMessageEnrichment(
                        "after-first-sync-names:" + (stage ?? string.Empty));
                }
                finally
                {
                    host.ClearFirstSyncUnlockInFlight();
                }
            }
        }
    }
}
