// =============================================================================
// FullHistoryAckDeadlineOwner
//
// Owns IFullHistoryAckDeadlineHost over the on-demand registry and arms the
// deadline off the caller's thread.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class FullHistoryAckDeadlineOwner : IFullHistoryAckDeadlineHost
    {
        private readonly HistoryOnDemandRegistry _registry;
        private readonly Func<TimeSpan> _deadline;
        private readonly Func<bool> _isBannerActive;
        private readonly Action<string, bool> _finishCatchUp;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Action<string> _log;

        public FullHistoryAckDeadlineOwner(
            HistoryOnDemandRegistry registry,
            Func<TimeSpan> deadline,
            Func<bool> isBannerActive,
            Action<string, bool> finishCatchUp,
            IRuntimeDiagnostics diagnostics,
            Action<string> log)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _deadline = deadline ?? throw new ArgumentNullException(nameof(deadline));
            _isBannerActive = isBannerActive ?? throw new ArgumentNullException(nameof(isBannerActive));
            _finishCatchUp = finishCatchUp ?? throw new ArgumentNullException(nameof(finishCatchUp));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public void Arm(string requestId)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await FullHistoryAckDeadlineDispatch.RunAsync(this, requestId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log("FULL_HISTORY ack deadline failed for " + requestId + ": " + ex.Message);
                }
            });
        }

        TimeSpan IFullHistoryAckDeadlineHost.FullHistoryAckDeadline => _deadline();

        Task IFullHistoryAckDeadlineHost.DelayAsync(TimeSpan delay) => Task.Delay(delay);

        HistoryOnDemandRequest IFullHistoryAckDeadlineHost.FindFullRequest(string requestId)
        {
            HistoryOnDemandRequest request = _registry.Find(requestId);
            return request != null && request.Kind == HistoryOnDemandKind.FullHistory ? request : null;
        }

        bool IFullHistoryAckDeadlineHost.TryClearFullRequest(HistoryOnDemandRequest request) =>
            _registry.TryClear(request);

        bool IFullHistoryAckDeadlineHost.IsFullHistoryPending
        {
            get
            {
                _registry.ReadFullDiagnostics(out bool pending, out string _, out HistoryOnDemandRequest _);
                return pending;
            }
        }

        bool IFullHistoryAckDeadlineHost.IsHistoryCatchUpBannerActive => _isBannerActive();

        void IFullHistoryAckDeadlineHost.FinishHistoryCatchUp(string reason, bool releaseEnrichment) =>
            _finishCatchUp(reason, releaseEnrichment);

        void IFullHistoryAckDeadlineHost.WriteHistoryJournal(string eventName, string detail) =>
            _diagnostics.Write("history", eventName, detail);
    }
}
