// =============================================================================
// IStatusWireActions
//
// Status publishing that goes through the UWP bridge socket. The portable
// StatusFacade calls this port; the adapter forwards to IWhatsAppSocket.
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IStatusWireActions
    {
        /// <summary>Null socket: publishing must fail rather than pretend it was sent.</summary>
        bool CanPublish { get; }

        /// <summary>Sends a text Status to status@broadcast. Returns the message id.</summary>
        Task<string> SendTextStatusAsync(
            string text,
            uint backgroundArgb,
            uint textArgb,
            int font,
            IList<string> statusJidList);
    }
}
