// =============================================================================
// UserLogoutDispatch
//
// Bounded server logout notice, then always wipe local session. Single-flight
// on the host so Settings cannot stack Disconnect clicks.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class UserLogoutDispatch
    {
        public static readonly TimeSpan DefaultNotifyTimeout = TimeSpan.FromSeconds(3);

        public static async Task RunAsync(
            IUserLogoutHost host,
            string reason = null,
            TimeSpan? notifyTimeout = null)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.TryBeginLogout())
            {
                host.JournalLogout("logout-already-in-flight", reason);
                return;
            }

            try
            {
                string logoutReason = reason ?? "user-initiated";
                TimeSpan timeout = notifyTimeout ?? DefaultNotifyTimeout;

                host.JournalLogout("logout-notify-begin", "reason=" + logoutReason);
                Task notify = host.NotifyServerLogoutAsync(logoutReason);
                Task winner = await Task.WhenAny(notify, Task.Delay(timeout))
                    .ConfigureAwait(false);

                if (winner != notify)
                {
                    host.JournalLogout(
                        "logout-notify-timeout",
                        "timeoutMs=" + (int)timeout.TotalMilliseconds);
                }
                else
                {
                    // Surface notify faults without blocking the wipe.
                    try
                    {
                        await notify.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        host.JournalLogout(
                            "logout-notify-failed",
                            "error=" + (ex.Message ?? string.Empty));
                    }

                    host.JournalLogout("logout-notify-end", "reason=" + logoutReason);
                }

                host.JournalLogout("clear-session-begin", "reason=" + logoutReason);
                await host.ClearSessionAsync().ConfigureAwait(false);
                await host.ClearPublishedContactsAsync().ConfigureAwait(false);
                await host.AutoRestoreDefaultAsync().ConfigureAwait(false);
            }
            finally
            {
                host.EndLogout();
            }
        }
    }
}
