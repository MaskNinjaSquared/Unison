// =============================================================================
// HistorySyncWireOwner
//
// Owns IHistorySyncWireHost. Session gates / diagnostics / HistoryFacade apply
// stay as ctor ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class HistorySyncWireOwner : IHistorySyncWireHost
    {
        private readonly Func<bool> _fatalSessionEnded;
        private readonly Func<bool> _hasAuthState;
        private readonly Action<int, string> _writeBegin;
        private readonly Action<int, long> _writeEnd;
        private readonly Func<HistorySync, Task> _apply;

        public HistorySyncWireOwner(
            Func<bool> fatalSessionEnded,
            Func<bool> hasAuthState,
            Action<int, string> writeBegin,
            Action<int, long> writeEnd,
            Func<HistorySync, Task> apply)
        {
            _fatalSessionEnded = fatalSessionEnded
                ?? throw new ArgumentNullException(nameof(fatalSessionEnded));
            _hasAuthState = hasAuthState
                ?? throw new ArgumentNullException(nameof(hasAuthState));
            _writeBegin = writeBegin ?? throw new ArgumentNullException(nameof(writeBegin));
            _writeEnd = writeEnd ?? throw new ArgumentNullException(nameof(writeEnd));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        }

        public void Run(HistorySync sync) =>
            HistorySyncWireApply.Run(this, sync);

        bool IHistorySyncWireHost.FatalSessionEnded => _fatalSessionEnded();

        bool IHistorySyncWireHost.HasAuthState => _hasAuthState();

        void IHistorySyncWireHost.WriteSyncApplyBegin(int conversations, string syncType) =>
            _writeBegin(conversations, syncType);

        void IHistorySyncWireHost.WriteSyncApplyEnd(int conversations, long elapsedMs) =>
            _writeEnd(conversations, elapsedMs);

        Task IHistorySyncWireHost.ApplyHistorySyncAsync(HistorySync sync) =>
            _apply(sync);
    }
}
