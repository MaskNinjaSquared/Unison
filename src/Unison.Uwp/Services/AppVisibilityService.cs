// =============================================================================
// AppVisibilityService
//
// Reports App.IsWindowVisible without letting portable façades name App / Window.
// App raises Changed through NotifyWindowVisibilityChanged when CoreWindow flips.
// =============================================================================
using System;
using Unison.Core.Contracts;

namespace Unison.Uwp.Services
{
    public sealed class AppVisibilityService : IAppVisibility
    {
        public bool IsWindowVisible => App.IsWindowVisible;

        public event EventHandler Changed;

        internal void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
