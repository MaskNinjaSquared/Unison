using System;
using Unison.Core.Models;
using Unison.Uwp.Helpers;
using Unison.Uwp.UI.Controls;
using Unison.Uwp.UI.Converters;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Fills the "My status" header row shared by every shell's Status list:
    /// status ring + thumb when the account has active statuses, plain avatar otherwise.
    /// </summary>
    internal static class MyStatusRowChrome
    {
        private static readonly RelativeTimestampConverter RelativeTime = new RelativeTimestampConverter();

        public static void Apply(
            StatusAuthorItem self,
            StatusListAvatarControl ring,
            FrameworkElement plainAvatar,
            TextBlock caption,
            TextBlock time)
        {
            bool hasSelf = self != null;
            ring.Visibility = hasSelf ? Visibility.Visible : Visibility.Collapsed;
            plainAvatar.Visibility = hasSelf ? Visibility.Collapsed : Visibility.Visible;
            if (hasSelf)
            {
                ring.AvatarUrl = self.AvatarUrl;
                ring.ViewState = self.ViewState;
            }

            caption.Text = hasSelf
                ? self.ViewStateCaption ?? string.Empty
                : LocalizedStrings.Get("StatusList_MyStatusNone", "No updates");
            time.Text = hasSelf
                ? (RelativeTime.Convert(self.LatestTimestampUtc, typeof(string), null, null) as string) ?? string.Empty
                : string.Empty;
        }

        /// <summary>The row reads as selected while the viewer shows the account's own statuses.</summary>
        public static bool IsSelfSelected(StatusAuthorItem selected, StatusAuthorItem self) =>
            selected != null && self != null &&
            string.Equals(selected.Jid, self.Jid, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the tap started on <paramref name="ancestor"/> (e.g. the "+" badge).</summary>
        public static bool IsWithin(object originalSource, DependencyObject ancestor)
        {
            var node = originalSource as DependencyObject;
            while (node != null)
            {
                if (ReferenceEquals(node, ancestor))
                {
                    return true;
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return false;
        }
    }
}
