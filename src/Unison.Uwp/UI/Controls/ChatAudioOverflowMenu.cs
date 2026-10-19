using System;
using Unison.Core.ViewModels;
using Unison.Uwp.Helpers;
using Unison.Uwp.UI.Helpers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>Shared "…" flyout for ready audio: Play audio + Save as….</summary>
    internal static class ChatAudioOverflowMenu
    {
        public static void Show(FrameworkElement anchor, ChatMessageViewModel vm, Action play)
        {
            if (anchor == null || vm == null || !vm.HasLocalAudio)
            {
                return;
            }

            string playLabel = LocalizedStrings.Get("ChatDetail_AudioPlay.Label", "Play audio");
            string saveLabel = LocalizedStrings.Get("ChatDetail_DocumentSaveAs.Label", "Save as…");
            FontFamily iconFont = null;
            try
            {
                iconFont = (FontFamily)Application.Current.Resources["IconFont"];
            }
            catch
            {
            }

            var flyout = new MenuFlyout();
            MessageBubbleContextActions.Add(flyout, vm);

            var playItem = new MenuFlyoutItem { Text = playLabel };
            if (iconFont != null)
            {
                playItem.Icon = new FontIcon { Glyph = "\uE768", FontFamily = iconFont };
            }

            playItem.Click += (_, __) => play?.Invoke();
            flyout.Items.Add(playItem);

            if (vm.CanSaveAudioAs)
            {
                var saveItem = new MenuFlyoutItem
                {
                    Text = saveLabel,
                    Command = vm.SaveAudioAsCommand
                };
                if (iconFont != null)
                {
                    saveItem.Icon = new FontIcon { Glyph = "\uE792", FontFamily = iconFont };
                }

                flyout.Items.Add(saveItem);
            }

            try
            {
                flyout.ShowAt(anchor);
            }
            catch
            {
            }
        }
    }
}
