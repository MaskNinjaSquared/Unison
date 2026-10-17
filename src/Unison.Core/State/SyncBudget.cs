// =============================================================================
// SyncBudget
//
// Whether startup and sync work should use the frugal budget: smaller batches,
// longer quiet floors, gentler list updates. Memory pressure decides on any
// device; on a phone with headroom, a hot sync does too. A desktop with headroom
// stays generous.
// =============================================================================
using System;

namespace Unison.Core.State
{
    public struct SyncBudgetInputs
    {
        /// <summary>The app's memory usage level is Low (plenty left).</summary>
        public bool HasMemoryHeadroom;

        public bool IsMobile;

        public int ChatCount;

        public bool IsInitialSyncActive;

        public bool IsReplayDrainActive;

        public bool IsFullHistoryInFlight;
    }

    public sealed class SyncBudget
    {
        /// <summary>A list this long is heavy enough on a phone to go frugal by itself.</summary>
        private const int HeavyMobileChatCount = 80;

        private readonly IChatStateStore _chatState;
        private readonly InitialSyncProgressState _initialSync;
        private readonly SyncWorkGate _syncWork;
        private readonly HistoryOnDemandRegistry _history;
        private readonly Func<bool> _hasMemoryHeadroom;
        private readonly bool _isMobile;

        /// <param name="hasMemoryHeadroom">Read on every check; memory pressure changes while running.</param>
        public SyncBudget(
            IChatStateStore chatState,
            InitialSyncProgressState initialSync,
            SyncWorkGate syncWork,
            HistoryOnDemandRegistry history,
            Func<bool> hasMemoryHeadroom,
            bool isMobile)
        {
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _initialSync = initialSync ?? throw new ArgumentNullException(nameof(initialSync));
            _syncWork = syncWork ?? throw new ArgumentNullException(nameof(syncWork));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _hasMemoryHeadroom = hasMemoryHeadroom ?? throw new ArgumentNullException(nameof(hasMemoryHeadroom));
            _isMobile = isMobile;
        }

        public bool IsFrugal => PreferFrugal(new SyncBudgetInputs
        {
            HasMemoryHeadroom = _hasMemoryHeadroom(),
            IsMobile = _isMobile,
            ChatCount = _chatState.Chats.Count,
            IsInitialSyncActive = _initialSync.IsActive,
            IsReplayDrainActive = _syncWork.IsReplayDrainActive,
            IsFullHistoryInFlight = _history.IsFullInFlight
        });

        public static bool PreferFrugal(SyncBudgetInputs inputs)
        {
            if (!inputs.HasMemoryHeadroom)
            {
                return true;
            }

            if (!inputs.IsMobile)
            {
                return false;
            }

            return inputs.ChatCount >= HeavyMobileChatCount
                || inputs.IsInitialSyncActive
                || inputs.IsReplayDrainActive
                || inputs.IsFullHistoryInFlight;
        }
    }
}
