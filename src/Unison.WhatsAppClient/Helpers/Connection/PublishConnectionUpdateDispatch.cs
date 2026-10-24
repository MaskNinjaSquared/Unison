// =============================================================================
// PublishConnectionUpdateDispatch
//
// Body of PublishConnectionUpdate. Toast / diagnostics I/O stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PublishConnectionUpdateDispatch
    {
        public static void Run(IPublishConnectionUpdateHost host, string status)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.CurrentConnectionStatus = status ?? string.Empty;
            if (host.ShouldClearReconnectRequiredToast(host.CurrentConnectionStatus))
            {
                host.SetSuppressReconnectToast(false);
                host.ClearReconnectRequiredToast(host.CurrentConnectionStatus);
            }

            host.StampLastConnectionEventUtc();
            host.WriteConnectionStatusJournal(host.CurrentConnectionStatus);
            host.LogConnectionStatus(host.CurrentConnectionStatus);
            host.RaiseConnectionUpdate(host.CurrentConnectionStatus);
        }
    }
}
