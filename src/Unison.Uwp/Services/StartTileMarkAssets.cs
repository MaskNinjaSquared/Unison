using System;
using Unison.Core.Models;

namespace Unison.Uwp.Services
{
    /// <summary>Package logos for each Start tile mark. Glyphs sit on the accent plate.</summary>
    internal static class StartTileMarkAssets
    {
        public static Uri Logo(StartTileMark mark)
        {
            if (mark == StartTileMark.WhatsAppWeb)
            {
                return new Uri("ms-appx:///Assets/Square44x44Logo.altform-unplated_targetsize-256.png");
            }

            if (mark == StartTileMark.WhatsAppLegacy)
            {
                return new Uri("ms-appx:///Assets/LegacyLogo/WhatsAppLogo.png");
            }

            return new Uri("ms-appx:///Assets/Square150x150Logo.png");
        }
    }
}
