// =============================================================================
// ISessionInitializedHost
//
// Side effects for SessionInitializedDispatch (OnSessionInitialized).
// =============================================================================
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface ISessionInitializedHost
    {
        bool FatalSessionEnded { get; }

        void MarkSessionEstablished();

        void ClearPairingRestartPending();

        void EnablePreSessionCloseAsFatal();

        void ResetPreSessionCloseStreak();

        void CompleteSessionEstablishedWait();

        void EnsureSelfPhonePersisted();

        Task PersistAuthStateForSessionAsync();

        bool ShouldDeferReconnectReplayWork();

        void LogDeferringReconnectWork();

        void SchedulePostReplayMaintenance();

        /// <summary>Fire-and-forget force-history repair (same as the former wire handler).</summary>
        void StartMessageStoreForceHistoryRepair(string reason);

        void RaiseSessionInitialized();

        void PairingTrace(string line);

        void LogSessionInitialized();
    }
}
