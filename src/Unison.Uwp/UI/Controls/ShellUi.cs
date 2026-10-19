using System;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Resolves the active <see cref="AppShell"/> for template hosts that swap Unison vs WhatsApp chrome.
    /// </summary>
    internal static class ShellUi
    {
        public static AppShell CurrentShell
        {
            get
            {
                try
                {
                    ILocalSettings settings = App.Services?.GetService<ILocalSettings>();
                    if (settings == null)
                    {
                        return AppShell.Unison;
                    }

                    int raw = settings.Get<int>(LocalSettingsConstants.SelectedShell);
                    return Enum.IsDefined(typeof(AppShell), raw) ? (AppShell)raw : AppShell.Unison;
                }
                catch
                {
                    return AppShell.Unison;
                }
            }
        }

        public static bool IsUnison => CurrentShell == AppShell.Unison;

        public static bool IsWhatsAppLegacy => CurrentShell == AppShell.WhatsAppLegacy;

        /// <summary>Unison bubble shape. Legacy reuses it; WhatsApp keeps its own masks.</summary>
        public static bool UsesUnisonBalloons => IsUnison || IsWhatsAppLegacy;
    }
}
