// =============================================================================
// ClearSessionDispatch
//
// Body of ClearSessionAsync. Store / socket / UI wipe I/O stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ClearSessionDispatch
    {
        public static async Task RunAsync(IClearSessionHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.LogClearSession("Hardening session wipe...");
            host.CaptureKeyStoreBeforeDisconnect();

            // Block “Unison desconectado” before tearing the socket down — otherwise the
            // background broker sees close while AuthStore still says Registered+MeId.
            host.SuppressReconnectToastForClear();
            host.ClearReconnectRequiredToast();

            await host.ClearAuthStoreEarlyAsync().ConfigureAwait(false);
            host.NullAuthState();

            // 1. Stop traffic quickly (sync) so UI can leave Connected without waiting on disk.
            host.ArmFatalDisconnectFlagsForClear();
            host.StopConnectionHealthMonitor("clear-session");
            host.DisconnectAndDisposeSocket();
            host.ResetSessionEstablishedSignal();

            // Show Login surface immediately — do NOT start Connect/QR until keys are gone.
            host.LogClearSession("Switching UI to Login before auth wipe.");
            await host.RaiseSessionClearedAsync(startPairing: false).ConfigureAwait(false);
            await host.YieldAsync().ConfigureAwait(false);

            // 2. Clear the FileKeyStore so reset is a true zero-state wipe.
            await host.ClearFileKeyStoreAsync().ConfigureAwait(false);

            // 3. AuthStore already cleared above (before disconnect); keep idempotent clear.
            await host.ClearAuthStoreIdempotentAsync().ConfigureAwait(false);

            // 4. The auth state was already dropped before the socket came down. It is
            // deliberately not nulled again here: switching the UI to Login above starts a
            // pairing attempt, and that attempt builds a fresh state to show a QR with. A
            // second null at this point lands in the middle of it and is what the connect
            // then dereferences.
            host.DropSharedKeyStoreAndResumeFlags();

            // 5. Drop Noise session material before pairing so Connect is a clean path.
            await host.ClearNoiseSessionAsync().ConfigureAwait(false);

            // Auth gone — restart pairing / QR now.
            // Clear the fatal latch so ConnectAsync for QR is allowed.
            host.AllowPairingAfterAuthWipe();
            host.LogClearSession("Auth wiped; starting pairing/QR.");
            await host.RaiseSessionClearedAsync(startPairing: true).ConfigureAwait(false);
            await host.YieldAsync().ConfigureAwait(false);

            // 6. Wipe messages, chats, and contact names from disk (epoch rotate — non-blocking for QR).
            await host.WipeMessageStoreAsync().ConfigureAwait(false);
            // history_migration / history_chat_preview: HistoryFacade via NotifySessionCleared.
            await host.ClearGroupRosterAsync().ConfigureAwait(false);

            // 7. Clear in-memory state
            await host.ClearInMemorySessionStateAsync().ConfigureAwait(false);
            host.ClearNotifications();

            host.LogClearSession("Session wipe complete.");
        }
    }
}
