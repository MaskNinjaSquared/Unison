// =============================================================================
// SilentFullHistoryChainDispatch
//
// Sequential silent FULL_HISTORY: fire only when the previous lot is quiet, wait
// the tier delay, then request with isFreshnessRepair false. Banner when a lot
// applied messages (including residual chunks after the empty-cap stops probing).
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class SilentFullHistoryChainDispatch
    {
        public static void Arm(ISilentFullHistoryChainHost host, string reason)
        {
            if (host == null)
            {
                return;
            }

            host.MarkConsecutiveEmptySilentLots(0);
            host.MarkSilentChainArmed(true);
            host.WriteSilentJournal("arm", reason ?? string.Empty);
            Schedule(host, SilentFullHistoryPolicy.ReasonPrefix + (reason ?? "post-init"), immediate: true);
        }

        public static void OnLotQuiet(ISilentFullHistoryChainHost host, string triggerReason, int appliedCount)
        {
            if (host == null)
            {
                return;
            }

            bool silent = SilentFullHistoryPolicy.IsSilentReason(triggerReason);
            if (!silent)
            {
                return;
            }

            host.MarkPreviousSilentLotQuiet(true);
            host.WriteSilentJournal(
                "lot-quiet",
                "applied=" + appliedCount +
                "; armed=" + host.IsSilentChainArmed +
                "; emptyStreak=" + host.ConsecutiveEmptySilentLots +
                "; trigger=" + (triggerReason ?? string.Empty));

            // Surface background catch-up whenever this silent lot applied data —
            // including residual applies after the chain was capped/disarmed.
            if (SilentFullHistoryPolicy.ShouldShowBanner(appliedCount))
            {
                host.BeginHistoryCatchUpBanner(
                    triggerReason ?? SilentFullHistoryPolicy.ReasonPrefix + "applied");
            }
            else
            {
                host.EndHistoryCatchUpBanner(SilentFullHistoryPolicy.ReasonPrefix + "empty");
            }

            if (!host.IsSilentChainArmed)
            {
                host.WriteSilentJournal("lot-quiet-disarmed", "no-reschedule");
                return;
            }

            int nextEmpty = SilentFullHistoryPolicy.NextConsecutiveEmpty(
                host.ConsecutiveEmptySilentLots,
                appliedCount);
            host.MarkConsecutiveEmptySilentLots(nextEmpty);

            SilentFullHistoryNext next = SilentFullHistoryPolicy.AfterQuietLot(
                lotWasSilent: true,
                fullClaimed: host.IsFullHistoryClaimed,
                consecutiveEmptyAfterThisLot: nextEmpty,
                out string decideReason);
            host.WriteSilentJournal(
                "after-quiet",
                decideReason + "; emptyStreak=" + nextEmpty);

            if (next == SilentFullHistoryNext.CapReached)
            {
                Stop(host, "empty-cap");
                return;
            }

            if (next == SilentFullHistoryNext.ScheduleAfterDelay)
            {
                Schedule(
                    host,
                    SilentFullHistoryPolicy.ReasonPrefix + "next",
                    immediate: false);
            }
        }

        public static void Stop(ISilentFullHistoryChainHost host, string reason)
        {
            if (host == null)
            {
                return;
            }

            host.CancelSilentSchedule();
            host.MarkSilentChainArmed(false);
            host.WriteSilentJournal("stop", reason ?? string.Empty);
        }

        private static void Schedule(ISilentFullHistoryChainHost host, string fireReason, bool immediate)
        {
            host.CancelSilentSchedule();
            CancellationTokenSource cts = host.CreateSilentScheduleCts();
            CancellationToken token = cts.Token;
            TimeSpan delay = immediate
                ? TimeSpan.FromSeconds(2)
                : SilentFullHistoryPolicy.DelayFor(host.DeviceCategory);

            host.StartSilentScheduleTask(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await host.DelayAsync(delay, token).ConfigureAwait(false);
                    }

                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    await FireAsync(host, fireReason).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    host.LogSilentChainFailed(ex.Message);
                }
            });
        }

        private static async Task FireAsync(ISilentFullHistoryChainHost host, string reason)
        {
            if (!host.IsSilentChainArmed)
            {
                host.WriteSilentJournal("skip-fire", "disarmed");
                return;
            }

            if (!SilentFullHistoryPolicy.CanFire(
                    host.IsSocketReadyForSilentFull,
                    host.IsHistoryCatchUpActive,
                    host.IsFullHistoryClaimed,
                    host.IsPreviousSilentLotQuiet,
                    out string gate))
            {
                host.WriteSilentJournal("skip-fire", gate);
                // Retry after the tier delay — meta may still be busy.
                Schedule(host, reason, immediate: false);
                return;
            }

            host.MarkPreviousSilentLotQuiet(false);
            host.WriteSilentJournal("fire", reason ?? string.Empty);
            bool ok = await host.RequestSilentFullHistoryAsync(reason).ConfigureAwait(false);
            host.WriteSilentJournal("fire-result", "ok=" + ok);
            if (!ok)
            {
                host.MarkPreviousSilentLotQuiet(true);
                if (host.IsSilentChainArmed)
                {
                    Schedule(host, reason, immediate: false);
                }
            }
        }
    }
}
