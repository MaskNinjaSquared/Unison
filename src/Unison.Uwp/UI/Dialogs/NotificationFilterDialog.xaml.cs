using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Unison.Core.Models;

namespace Unison.Uwp.UI.Dialogs
{
    public sealed partial class NotificationFilterDialog : ContentDialog
    {
        public LockScreenNotificationScope Result { get; private set; }

        public NotificationFilterDialog()
        {
            InitializeComponent();
        }

        public void Bind(LockScreenNotificationScope current)
        {
            Result = current;
            OptionFor(current).IsChecked = true;
        }

        private void OnScopeChecked(object sender, RoutedEventArgs e)
        {
            Result = ReadSelected();
        }

        private void OnOkClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            Result = ReadSelected();
        }

        private LockScreenNotificationScope ReadSelected()
        {
            if (GroupsOption.IsChecked == true)
            {
                return LockScreenNotificationScope.Groups;
            }

            if (DirectOption.IsChecked == true)
            {
                return LockScreenNotificationScope.Direct;
            }

            if (FavoritesOption.IsChecked == true)
            {
                return LockScreenNotificationScope.Favorites;
            }

            return LockScreenNotificationScope.All;
        }

        private RadioButton OptionFor(LockScreenNotificationScope scope)
        {
            if (scope == LockScreenNotificationScope.Groups)
            {
                return GroupsOption;
            }

            if (scope == LockScreenNotificationScope.Direct)
            {
                return DirectOption;
            }

            if (scope == LockScreenNotificationScope.Favorites)
            {
                return FavoritesOption;
            }

            return AllOption;
        }
    }
}
