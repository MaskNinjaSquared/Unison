// =============================================================================
// DeferredStartupMaintenanceOwner
//
// Owns IDeferredStartupMaintenanceHost. Quiet/memory waits, name tables, and
// façade contact passes stay as ctor ports.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class DeferredStartupMaintenanceOwner : IDeferredStartupMaintenanceHost
    {
        private readonly Func<TimeSpan> _quietWait;
        private readonly Func<bool> _useFrugal;
        private readonly Func<bool> _isWindowVisible;
        private readonly Func<int> _chatCount;
        private readonly Func<bool> _isAuthRegistered;
        private readonly Func<bool> _isHistoryCatchUpActive;
        private readonly Func<string> _memoryUsageLevelText;
        private readonly Func<Task> _hydrateCachedAvatarsEarlyAsync;
        private readonly Action<Exception> _logAvatarHydrateFailed;
        private readonly Func<TimeSpan, CancellationToken, Task> _waitForStartupQuietAsync;
        private readonly Func<CancellationToken, Task<bool>> _waitForMemoryHeadroomAsync;
        private readonly Action<string> _writeDeferredSkipped;
        private readonly Action _clearSyncStatus;
        private readonly Func<Task<IReadOnlyDictionary<string, string>>> _loadStoredContactNamesAsync;
        private readonly Func<Task<IReadOnlyDictionary<string, string>>> _loadStoredPhoneContactNamesAsync;
        private readonly Func<string, string, string> _sanitizeContactLabel;
        private readonly Action<string, string> _tryAddPushName;
        private readonly Action<string, string> _tryAddPhoneContactName;
        private readonly Action _logEmptyStartupCatalog;
        private readonly Func<Task> _notifyDeferredStartupListRepairAsync;
        private readonly Func<Task> _normalizePersistedChatNamesAsync;
        private readonly Func<bool, Task> _runDeferredStartupContactPassAsync;
        private readonly Action<Exception> _logDeferredMaintenanceFailed;

        public DeferredStartupMaintenanceOwner(
            Func<TimeSpan> quietWait,
            Func<bool> useFrugal,
            Func<bool> isWindowVisible,
            Func<int> chatCount,
            Func<bool> isAuthRegistered,
            Func<bool> isHistoryCatchUpActive,
            Func<string> memoryUsageLevelText,
            Func<Task> hydrateCachedAvatarsEarlyAsync,
            Action<Exception> logAvatarHydrateFailed,
            Func<TimeSpan, CancellationToken, Task> waitForStartupQuietAsync,
            Func<CancellationToken, Task<bool>> waitForMemoryHeadroomAsync,
            Action<string> writeDeferredSkipped,
            Action clearSyncStatus,
            Func<Task<IReadOnlyDictionary<string, string>>> loadStoredContactNamesAsync,
            Func<Task<IReadOnlyDictionary<string, string>>> loadStoredPhoneContactNamesAsync,
            Func<string, string, string> sanitizeContactLabel,
            Action<string, string> tryAddPushName,
            Action<string, string> tryAddPhoneContactName,
            Action logEmptyStartupCatalog,
            Func<Task> notifyDeferredStartupListRepairAsync,
            Func<Task> normalizePersistedChatNamesAsync,
            Func<bool, Task> runDeferredStartupContactPassAsync,
            Action<Exception> logDeferredMaintenanceFailed)
        {
            _quietWait = quietWait ?? throw new ArgumentNullException(nameof(quietWait));
            _useFrugal = useFrugal ?? throw new ArgumentNullException(nameof(useFrugal));
            _isWindowVisible = isWindowVisible
                ?? throw new ArgumentNullException(nameof(isWindowVisible));
            _chatCount = chatCount ?? throw new ArgumentNullException(nameof(chatCount));
            _isAuthRegistered = isAuthRegistered
                ?? throw new ArgumentNullException(nameof(isAuthRegistered));
            _isHistoryCatchUpActive = isHistoryCatchUpActive
                ?? throw new ArgumentNullException(nameof(isHistoryCatchUpActive));
            _memoryUsageLevelText = memoryUsageLevelText
                ?? throw new ArgumentNullException(nameof(memoryUsageLevelText));
            _hydrateCachedAvatarsEarlyAsync = hydrateCachedAvatarsEarlyAsync
                ?? throw new ArgumentNullException(nameof(hydrateCachedAvatarsEarlyAsync));
            _logAvatarHydrateFailed = logAvatarHydrateFailed
                ?? throw new ArgumentNullException(nameof(logAvatarHydrateFailed));
            _waitForStartupQuietAsync = waitForStartupQuietAsync
                ?? throw new ArgumentNullException(nameof(waitForStartupQuietAsync));
            _waitForMemoryHeadroomAsync = waitForMemoryHeadroomAsync
                ?? throw new ArgumentNullException(nameof(waitForMemoryHeadroomAsync));
            _writeDeferredSkipped = writeDeferredSkipped
                ?? throw new ArgumentNullException(nameof(writeDeferredSkipped));
            _clearSyncStatus = clearSyncStatus
                ?? throw new ArgumentNullException(nameof(clearSyncStatus));
            _loadStoredContactNamesAsync = loadStoredContactNamesAsync
                ?? throw new ArgumentNullException(nameof(loadStoredContactNamesAsync));
            _loadStoredPhoneContactNamesAsync = loadStoredPhoneContactNamesAsync
                ?? throw new ArgumentNullException(nameof(loadStoredPhoneContactNamesAsync));
            _sanitizeContactLabel = sanitizeContactLabel
                ?? throw new ArgumentNullException(nameof(sanitizeContactLabel));
            _tryAddPushName = tryAddPushName
                ?? throw new ArgumentNullException(nameof(tryAddPushName));
            _tryAddPhoneContactName = tryAddPhoneContactName
                ?? throw new ArgumentNullException(nameof(tryAddPhoneContactName));
            _logEmptyStartupCatalog = logEmptyStartupCatalog
                ?? throw new ArgumentNullException(nameof(logEmptyStartupCatalog));
            _notifyDeferredStartupListRepairAsync = notifyDeferredStartupListRepairAsync
                ?? throw new ArgumentNullException(nameof(notifyDeferredStartupListRepairAsync));
            _normalizePersistedChatNamesAsync = normalizePersistedChatNamesAsync
                ?? throw new ArgumentNullException(nameof(normalizePersistedChatNamesAsync));
            _runDeferredStartupContactPassAsync = runDeferredStartupContactPassAsync
                ?? throw new ArgumentNullException(nameof(runDeferredStartupContactPassAsync));
            _logDeferredMaintenanceFailed = logDeferredMaintenanceFailed
                ?? throw new ArgumentNullException(nameof(logDeferredMaintenanceFailed));
        }

        public Task RunAsync() => DeferredStartupMaintenanceDispatch.RunAsync(this);

        TimeSpan IDeferredStartupMaintenanceHost.DeferredStartupQuietWait => _quietWait();

        bool IDeferredStartupMaintenanceHost.UseFrugalSyncBudget => _useFrugal();

        bool IDeferredStartupMaintenanceHost.IsWindowVisible => _isWindowVisible();

        int IDeferredStartupMaintenanceHost.ChatCount => _chatCount();

        bool IDeferredStartupMaintenanceHost.IsAuthRegistered => _isAuthRegistered();

        bool IDeferredStartupMaintenanceHost.IsHistoryCatchUpActive =>
            _isHistoryCatchUpActive();

        string IDeferredStartupMaintenanceHost.MemoryUsageLevelText =>
            _memoryUsageLevelText();

        Task IDeferredStartupMaintenanceHost.HydrateCachedAvatarsEarlyAsync() =>
            _hydrateCachedAvatarsEarlyAsync();

        void IDeferredStartupMaintenanceHost.LogAvatarHydrateFailed(Exception ex) =>
            _logAvatarHydrateFailed(ex);

        Task IDeferredStartupMaintenanceHost.WaitForStartupQuietAsync(
            TimeSpan timeout,
            CancellationToken token) =>
            _waitForStartupQuietAsync(timeout, token);

        Task<bool> IDeferredStartupMaintenanceHost.WaitForMemoryHeadroomAsync(
            CancellationToken token) =>
            _waitForMemoryHeadroomAsync(token);

        void IDeferredStartupMaintenanceHost.WriteDeferredSkipped(string reasonDetail) =>
            _writeDeferredSkipped(reasonDetail);

        void IDeferredStartupMaintenanceHost.ClearSyncStatus() =>
            _clearSyncStatus();

        Task<IReadOnlyDictionary<string, string>> IDeferredStartupMaintenanceHost.LoadStoredContactNamesAsync() =>
            _loadStoredContactNamesAsync();

        Task<IReadOnlyDictionary<string, string>> IDeferredStartupMaintenanceHost.LoadStoredPhoneContactNamesAsync() =>
            _loadStoredPhoneContactNamesAsync();

        string IDeferredStartupMaintenanceHost.SanitizeContactLabel(string label, string jid) =>
            _sanitizeContactLabel(label, jid);

        void IDeferredStartupMaintenanceHost.TryAddPushName(string jid, string sanitized) =>
            _tryAddPushName(jid, sanitized);

        void IDeferredStartupMaintenanceHost.TryAddPhoneContactName(string jid, string trimmed) =>
            _tryAddPhoneContactName(jid, trimmed);

        void IDeferredStartupMaintenanceHost.LogEmptyStartupCatalog() =>
            _logEmptyStartupCatalog();

        Task IDeferredStartupMaintenanceHost.NotifyDeferredStartupListRepairAsync() =>
            _notifyDeferredStartupListRepairAsync();

        Task IDeferredStartupMaintenanceHost.NormalizePersistedChatNamesAsync() =>
            _normalizePersistedChatNamesAsync();

        Task IDeferredStartupMaintenanceHost.RunDeferredStartupContactPassAsync(
            bool resolveMissingNames) =>
            _runDeferredStartupContactPassAsync(resolveMissingNames);

        void IDeferredStartupMaintenanceHost.LogDeferredMaintenanceFailed(Exception ex) =>
            _logDeferredMaintenanceFailed(ex);
    }
}
