// =============================================================================
// UserResyncFullHistoryDispatch
//
// EnsureFullHistoryRequestedForUserResyncAsync: first FULL_HISTORY attempt, then
// optional fresh-transport retry. Connect / send / latch I/O stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class UserResyncFullHistoryDispatch
    {
        public static async Task<bool> RunAsync(IUserResyncFullHistoryHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                await host.EnsureConnectedAsync(
                        UserResyncFullHistoryGates.FirstConnectTimeoutMs,
                        forceFreshTransport: false)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.LogUserResync(
                    "EnsureConnected before user resync failed (" + reason + "): " + ex.Message);
            }

            bool firstOk = await host.RequestFullHistoryOnDemandTrackedAsync(
                    reason,
                    isFreshnessRepair: true)
                .ConfigureAwait(false);
            if (!UserResyncFullHistoryGates.NeedsFreshTransportRetry(firstOk))
            {
                host.LogUserResync("FULL_HISTORY requested for user resync (" + reason + ").");
                return true;
            }

            host.LogUserResync(
                "FULL_HISTORY not sent on live socket (" + reason +
                "); forcing fresh reconnect + retry.");
            host.RaiseUserResyncStatus("Reconnecting to reload history...");

            try
            {
                // Reset the per-session gate; otherwise a failed first attempt that flipped
                // the flag without a real send would block retry.
                host.ReleaseFullHistoryRequestGate();

                await host.EnsureConnectedAsync(
                        UserResyncFullHistoryGates.FreshConnectTimeoutMs,
                        forceFreshTransport: true)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.LogUserResync(
                    "Fresh reconnect for user resync failed (" + reason + "): " + ex.Message);
                return false;
            }

            await Task.Delay(UserResyncFullHistoryGates.PostReconnectSettle).ConfigureAwait(false);

            bool secondOk = await host.RequestFullHistoryOnDemandTrackedAsync(
                    reason + ":after-reconnect",
                    isFreshnessRepair: true)
                .ConfigureAwait(false);
            if (secondOk)
            {
                host.LogUserResync("FULL_HISTORY requested after reconnect (" + reason + ").");
            }

            if (!secondOk)
            {
                await host.TryConsumeMessageStoreForceHistoryRepairAsync(
                        reason + ":post-reconnect-consume")
                    .ConfigureAwait(false);
            }

            return UserResyncFullHistoryGates.SucceededAfterRetry(
                secondOk,
                host.IsFullHistoryClaimed);
        }
    }
}
