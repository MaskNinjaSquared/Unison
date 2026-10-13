using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.StartScreen;
using Windows.UI.ViewManagement;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Pins a single Start tile with the chosen glyph on the system accent color.
    /// Updating an existing pin keeps the same tile id, so badges already routed there stay.
    /// </summary>
    public sealed class StartTilePinService : IStartTilePinService
    {
        public async Task<bool> PinAsync(StartTileMark mark)
        {
            Uri logo = StartTileMarkAssets.Logo(mark);
            Color accent = new UISettings().GetColorValue(UIColorType.Accent);

            try
            {
                if (SecondaryTile.Exists(StartTileMirror.TileId))
                {
                    var existing = new SecondaryTile(StartTileMirror.TileId)
                    {
                        DisplayName = "Unison",
                        Arguments = "app"
                    };
                    ApplyChrome(existing, logo, accent);
                    return await existing.UpdateAsync();
                }

                var tile = new SecondaryTile(
                    StartTileMirror.TileId,
                    "Unison",
                    "app",
                    logo,
                    TileSize.Square150x150)
                {
                    DisplayName = "Unison"
                };
                ApplyChrome(tile, logo, accent);
                return await tile.RequestCreateAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StartTilePinService] Pin failed: " + ex.Message);
                return false;
            }
        }

        private static void ApplyChrome(SecondaryTile tile, Uri logo, Color accent)
        {
            tile.VisualElements.Square150x150Logo = logo;
            tile.VisualElements.BackgroundColor = accent;
            tile.VisualElements.ShowNameOnSquare150x150Logo = true;
            tile.VisualElements.ForegroundText = Luminance(accent) > 160
                ? ForegroundText.Dark
                : ForegroundText.Light;
        }

        private static double Luminance(Color color)
        {
            return (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
        }
    }
}
