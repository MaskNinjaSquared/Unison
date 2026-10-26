// =============================================================================
// HandleDecryptedMessageDispatch
//
// Body of HandleDecryptedMessageAsync. Facade notify stays on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HandleDecryptedMessageDispatch
    {
        public static async Task RunAsync(
            IHandleDecryptedMessageHost host,
            IncomingDecryptedNotifyRequest request)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                host.SetIncomingMessagePumpStage("routing", request);
                if (request != null && !request.IsOffline)
                {
                    host.LogLiveInboundHandle(request);
                }

                if (!host.HasMessageFacade)
                {
                    host.WriteMessageFacadeMissing(request?.MessageId);
                    return;
                }

                if (request != null)
                {
                    request.RouteTrace = new IncomingRouteTrace();
                }

                host.SetIncomingMessagePumpStage("route", request);
                await host.NotifyIncomingDecryptedAsync(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.LogHandleDecryptedError(ex, request);
                host.RecordHandleDecryptedFailed(ex, request);
                throw;
            }
        }
    }
}
