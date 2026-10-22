// =============================================================================
// AppVisibilityService
//
// Reports App.IsWindowVisible without letting portable façades name App / Window.
// =============================================================================
using Unison.Core.Contracts;

namespace Unison.Uwp.Services
{
    public sealed class AppVisibilityService : IAppVisibility
    {
        public bool IsWindowVisible => App.IsWindowVisible;
    }
}
