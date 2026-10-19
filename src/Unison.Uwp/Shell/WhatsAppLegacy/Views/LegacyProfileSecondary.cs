using System;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// Fills the overflow "profile" AppBarButton with the current user name and avatar.
    /// </summary>
    internal static class LegacyProfileSecondary
    {
        public static void Apply(AppBarButton button, ShellViewModel shell)
        {
            if (button == null)
            {
                return;
            }

            string name = shell?.CurrentUserName;
            if (string.IsNullOrWhiteSpace(name))
            {
                var strings = App.Services?.GetService<IStringResources>();
                name = strings?.Get("Shell_LegacyMenu_Profile.Label", "Profile") ?? "Profile";
            }

            button.Label = name;

            string avatar = shell?.CurrentUserAvatar;
            if (!string.IsNullOrWhiteSpace(avatar))
            {
                try
                {
                    button.Icon = new BitmapIcon
                    {
                        UriSource = new Uri(avatar, UriKind.RelativeOrAbsolute),
                        ShowAsMonochrome = false
                    };
                    return;
                }
                catch
                {
                }
            }

            try
            {
                if (Application.Current.Resources.ContainsKey("AvatarContactFallbackImage"))
                {
                    var bmp = Application.Current.Resources["AvatarContactFallbackImage"] as BitmapImage;
                    if (bmp?.UriSource != null)
                    {
                        button.Icon = new BitmapIcon
                        {
                            UriSource = bmp.UriSource,
                            ShowAsMonochrome = false
                        };
                        return;
                    }
                }
            }
            catch
            {
            }

            button.Icon = new FontIcon
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Glyph = "\uE77B"
            };
        }
    }
}
