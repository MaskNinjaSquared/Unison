using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>
    /// Writes on-disk thumb/poster URIs onto history message rows before SQLite persist.
    /// WinRT / StorageFile work stays in the UWP adapter.
    /// </summary>
    public interface IHistoryMessageThumbnailMaterializer
    {
        Task MaterializeAsync(IList<HistoryMessage> messages);
    }
}
