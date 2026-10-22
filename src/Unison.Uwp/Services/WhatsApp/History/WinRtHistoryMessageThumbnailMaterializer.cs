// =============================================================================
// WinRtHistoryMessageThumbnailMaterializer
//
// Thin adapter so HistoryFacade does not name the static HistoryThumbnailMaterializer.
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.Uwp.Services.WhatsApp.History
{
    public sealed class WinRtHistoryMessageThumbnailMaterializer : IHistoryMessageThumbnailMaterializer
    {
        public Task MaterializeAsync(IList<HistoryMessage> messages)
        {
            return HistoryThumbnailMaterializer.MaterializeMessageThumbsAsync(messages);
        }
    }
}
