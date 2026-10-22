// =============================================================================
// WinRtStatusThumbnailMaterializer
//
// Thin adapter so StatusFacade (and HistoryFacade) do not name the static
// HistoryThumbnailMaterializer — keeps WinRT file I/O behind IStatusThumbnailMaterializer.
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Uwp.Services.WhatsApp.History;

namespace Unison.Uwp.Services.WhatsApp.Status
{
    public sealed class WinRtStatusThumbnailMaterializer : IStatusThumbnailMaterializer
    {
        public Task MaterializeAsync(IList<HistoryStatus> statuses)
        {
            return HistoryThumbnailMaterializer.MaterializeStatusThumbsAsync(statuses);
        }
    }
}
