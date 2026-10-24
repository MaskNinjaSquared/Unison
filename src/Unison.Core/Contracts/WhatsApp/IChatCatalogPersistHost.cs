// =============================================================================
// IChatCatalogPersistHost
//
// Side effects for ChatCatalogPersistDispatch (debounced catalogue save).
// PersistScheduler + timer + PersistDataAsync stay on the host.
// =============================================================================
using System.Threading.Tasks;
using Unison.Core.State;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IChatCatalogPersistHost
    {
        PersistScheduleAction RequestPersist();

        void LogDeferredDuringWarmup();

        /// <summary>Restart the debounce timer; elapsed should call OnDebounceElapsedAsync.</summary>
        void ArmDebounceTimer();

        bool TryBeginPersist();

        Task<bool> RunFullPersistAsync();

        void RestorePersistDebt();

        PersistEnableResult EnableAfterStartup();

        void LogSuppressionLifted(string reason);

        void LogFlushingDeferred(string reason);
    }
}
