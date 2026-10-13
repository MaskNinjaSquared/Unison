using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The stored tile mark is only accepted when it is one of the known options.
    /// Anything else, including a missing setting, is Unison.
    /// </summary>
    public static class StartTileMarkPolicy
    {
        public static StartTileMark Resolve(int stored)
        {
            if (stored == (int)StartTileMark.WhatsAppWeb)
            {
                return StartTileMark.WhatsAppWeb;
            }

            if (stored == (int)StartTileMark.WhatsAppLegacy)
            {
                return StartTileMark.WhatsAppLegacy;
            }

            return StartTileMark.Unison;
        }
    }
}
