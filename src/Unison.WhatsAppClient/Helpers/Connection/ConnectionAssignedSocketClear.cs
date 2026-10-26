// =============================================================================
// ConnectionAssignedSocketClear
//
// Shared dispose/clear of the assigned socket after open abort or failed
// transport connect. Journal text stays on the host.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public interface IConnectionAssignedSocketClearHost
    {
        void NoteConnectAborted(string journalReason);

        void DisposeAndClearAssignedSocket();
    }

    public static class ConnectionAssignedSocketClear
    {
        public static void Abort(IConnectionAssignedSocketClearHost host, string journalReason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.NoteConnectAborted(journalReason ?? string.Empty);
            host.DisposeAndClearAssignedSocket();
        }

        public static void AfterFailedConnect(IConnectionAssignedSocketClearHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.DisposeAndClearAssignedSocket();
        }
    }
}
