using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
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
