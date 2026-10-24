// =============================================================================
// DeferredStartupMaintenanceDispatch
//
// Body of RunDeferredStartupMaintenanceAsync after CAS start. Quiet/memory
// waits and store I/O stay on the host.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class DeferredStartupMaintenanceDispatch
    {
        public static async Task RunAsync(IDeferredStartupMaintenanceHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                try
                {
                    await host.HydrateCachedAvatarsEarlyAsync().ConfigureAwait(false);
                }
                catch (Exception exHydrate)
                {
                    host.LogAvatarHydrateFailed(exHydrate);
                }

                await host.WaitForStartupQuietAsync(
                        host.DeferredStartupQuietWait,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                DeferredStartupFrugalAction frugal = DeferredStartupFrugalGate.Decide(
                    host.UseFrugalSyncBudget,
                    host.IsWindowVisible);
                if (frugal == DeferredStartupFrugalAction.SkipVisibility)
                {
                    host.WriteDeferredSkipped("reason=visibility");
                    return;
                }

                if (frugal == DeferredStartupFrugalAction.CheckMemory)
                {
                    if (!await host.WaitForMemoryHeadroomAsync(CancellationToken.None)
                            .ConfigureAwait(false))
                    {
                        host.WriteDeferredSkipped(
                            "reason=memory; level=" + host.MemoryUsageLevelText);
                        host.ClearSyncStatus();
                        return;
                    }
                }

                IReadOnlyDictionary<string, string> storedNames =
                    await host.LoadStoredContactNamesAsync().ConfigureAwait(false);
                if (storedNames != null)
                {
                    foreach (KeyValuePair<string, string> kvp in storedNames)
                    {
                        string sanitized = host.SanitizeContactLabel(kvp.Value, kvp.Key);
                        if (!string.IsNullOrWhiteSpace(sanitized))
                        {
                            host.TryAddPushName(kvp.Key, sanitized);
                        }
                    }
                }

                IReadOnlyDictionary<string, string> storedPhoneNames =
                    await host.LoadStoredPhoneContactNamesAsync().ConfigureAwait(false);
                if (storedPhoneNames != null)
                {
                    foreach (KeyValuePair<string, string> kvp in storedPhoneNames)
                    {
                        if (!string.IsNullOrWhiteSpace(kvp.Value))
                        {
                            host.TryAddPhoneContactName(kvp.Key, kvp.Value.Trim());
                        }
                    }
                }

                if (host.ChatCount == 0 && host.IsAuthRegistered)
                {
                    host.LogEmptyStartupCatalog();
                }

                await host.NotifyDeferredStartupListRepairAsync().ConfigureAwait(false);
                await host.NormalizePersistedChatNamesAsync().ConfigureAwait(false);

                bool resolveMissing = DeferredStartupFrugalGate.ShouldResolveMissingNames(
                    host.ChatCount,
                    host.UseFrugalSyncBudget,
                    HistoryCatchUpLocalWorkPolicy.ShouldDeferHeavyLocalWork(
                        host.IsHistoryCatchUpActive));
                await host.RunDeferredStartupContactPassAsync(resolveMissing)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.LogDeferredMaintenanceFailed(ex);
            }
        }
    }
}
