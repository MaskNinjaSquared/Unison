using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>
    /// Writes on-disk poster/thumb URIs onto status rows before they hit the store.
    /// WinRT / StorageFile work stays in the UWP adapter — StatusFacade only awaits this port.
    /// </summary>
    public interface IStatusThumbnailMaterializer
    {
        Task MaterializeAsync(IList<HistoryStatus> statuses);
    }
}
