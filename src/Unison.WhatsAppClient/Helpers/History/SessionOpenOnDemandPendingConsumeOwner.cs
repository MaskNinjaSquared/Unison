// =============================================================================
// SessionOpenOnDemandPendingConsumeOwner
//
// Owns ISessionOpenOnDemandPendingConsumeHost: re-asks session-open OnDemand
// for JIDs latched across a crash, through History (not Messages) so the
// consume does not re-arm the crash latch.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class SessionOpenOnDemandPendingConsumeOwner : ISessionOpenOnDemandPendingConsumeHost
    {
        private const int ChatPageCount = 80;

        private readonly Func<IHistoryService> _history;
        private readonly Action<string> _claimSessionOpenOnDemand;
        private readonly Action<string> _log;

        public SessionOpenOnDemandPendingConsumeOwner(
            Func<IHistoryService> history,
            Action<string> claimSessionOpenOnDemand,
            Action<string> log)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _claimSessionOpenOnDemand = claimSessionOpenOnDemand
                ?? throw new ArgumentNullException(nameof(claimSessionOpenOnDemand));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public Task ConsumeAsync(string reason) => SessionOpenOnDemandPendingConsumeDispatch.RunAsync(this, reason);

        bool ISessionOpenOnDemandPendingConsumeHost.IsPairingBootstrapActive =>
            _history()?.IsPairingBootstrapActive ?? false;

        int ISessionOpenOnDemandPendingConsumeHost.PeekSessionOpenOnDemandPendingCount() =>
            _history()?.PeekSessionOpenOnDemandPendingCount() ?? 0;

        IReadOnlyList<string> ISessionOpenOnDemandPendingConsumeHost.TakeSessionOpenOnDemandPending() =>
            _history()?.TakeSessionOpenOnDemandPending() ?? Array.Empty<string>();

        void ISessionOpenOnDemandPendingConsumeHost.LogSessionOpenOnDemandPending(string message) => _log(message);

        void ISessionOpenOnDemandPendingConsumeHost.ClaimSessionOpenOnDemand(string jid) =>
            _claimSessionOpenOnDemand(jid);

        Task ISessionOpenOnDemandPendingConsumeHost.EnsureHistoryOnDemandAsync(string jid)
        {
            IHistoryService history = _history();
            if (history == null || string.IsNullOrWhiteSpace(jid))
            {
                return Task.CompletedTask;
            }

            return history.EnsureChatPageAsync(jid, count: ChatPageCount);
        }
    }
}
