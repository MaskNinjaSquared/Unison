// =============================================================================
// ConnectionUpdateRoute
//
// Maps a legacy connection-status string (+ whether the account is registered)
// onto which close/restart policy branch the host should run.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public enum ConnectionUpdateRouteKind
    {
        Other,

        Restart,

        RegisteredClose,

        UnregisteredClose
    }

    public static class ConnectionUpdateRoute
    {
        public static ConnectionUpdateRouteKind Classify(string status, bool registered)
        {
            if (string.Equals(status, "restart", StringComparison.Ordinal))
            {
                return ConnectionUpdateRouteKind.Restart;
            }

            if (string.Equals(status, "close", StringComparison.Ordinal))
            {
                return registered
                    ? ConnectionUpdateRouteKind.RegisteredClose
                    : ConnectionUpdateRouteKind.UnregisteredClose;
            }

            return ConnectionUpdateRouteKind.Other;
        }
    }
}
