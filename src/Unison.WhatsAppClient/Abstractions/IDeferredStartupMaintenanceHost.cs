// =============================================================================
// IDeferredStartupMaintenanceHost
//
// Side effects for DeferredStartupMaintenanceDispatch.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IDeferredStartupMaintenanceHost
    {
        TimeSpan DeferredStartupQuietWait { get; }

        bool UseFrugalSyncBudget { get; }

        bool IsWindowVisible { get; }

        int ChatCount { get; }

        bool IsAuthRegistered { get; }

        bool IsHistoryCatchUpActive { get; }

        string MemoryUsageLevelText { get; }

        Task HydrateCachedAvatarsEarlyAsync();

        void LogAvatarHydrateFailed(Exception ex);

        Task WaitForStartupQuietAsync(TimeSpan timeout, CancellationToken token);

        Task<bool> WaitForMemoryHeadroomAsync(CancellationToken token);

        void WriteDeferredSkipped(string reasonDetail);

        void ClearSyncStatus();

        Task<IReadOnlyDictionary<string, string>> LoadStoredContactNamesAsync();

        Task<IReadOnlyDictionary<string, string>> LoadStoredPhoneContactNamesAsync();

        string SanitizeContactLabel(string label, string jid);

        void TryAddPushName(string jid, string sanitized);

        void TryAddPhoneContactName(string jid, string trimmed);

        void LogEmptyStartupCatalog();

        Task NotifyDeferredStartupListRepairAsync();

        Task NormalizePersistedChatNamesAsync();

        Task RunDeferredStartupContactPassAsync(bool resolveMissingNames);

        void LogDeferredMaintenanceFailed(Exception ex);
    }
}
