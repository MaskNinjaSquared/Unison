using System;
using Unison.Core.Models;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Logos in Assets/CustomTiles for each Start tile mark.
    /// Medium (150) keeps the padded plate look; small (71) uses the original SmallTile assets.
    /// </summary>
    internal static class StartTileMarkAssets
    {
        public static Uri Logo(StartTileMark mark)
        {
            if (mark == StartTileMark.WhatsAppWeb)
            {
                return new Uri("ms-appx:///Assets/CustomTiles/WhatsAppWeb.png");
            }

            if (mark == StartTileMark.WhatsAppLegacy)
            {
                return new Uri("ms-appx:///Assets/CustomTiles/WhatsAppLegacy.png");
            }

            return new Uri("ms-appx:///Assets/CustomTiles/Unison.png");
        }

        public static Uri SmallLogo(StartTileMark mark)
        {
            if (mark == StartTileMark.WhatsAppWeb)
            {
                return new Uri("ms-appx:///Assets/CustomTiles/WhatsAppWeb.Small.png");
            }

            if (mark == StartTileMark.WhatsAppLegacy)
            {
                return new Uri("ms-appx:///Assets/CustomTiles/WhatsAppLegacy.Small.png");
            }

            return new Uri("ms-appx:///Assets/CustomTiles/Unison.Small.png");
        }
    }
}
