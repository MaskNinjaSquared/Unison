// =============================================================================
// GroupEphemeralDialog
//
// Radio picker for create-group disappearing messages.
// =============================================================================
using Unison.Core.Models;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Dialogs
{
    public sealed partial class GroupEphemeralDialog : ContentDialog
    {
        public GroupEphemeralDialog()
        {
            InitializeComponent();
        }

        public GroupEphemeralDuration SelectedDuration { get; private set; } =
            GroupEphemeralDuration.Off;

        public void SetCurrent(GroupEphemeralDuration current)
        {
            SelectedDuration = current;
            Option24Hours.IsChecked = current == GroupEphemeralDuration.Hours24;
            Option7Days.IsChecked = current == GroupEphemeralDuration.Days7;
            Option90Days.IsChecked = current == GroupEphemeralDuration.Days90;
            OptionOff.IsChecked = current == GroupEphemeralDuration.Off
                || (!Option24Hours.IsChecked.GetValueOrDefault()
                    && !Option7Days.IsChecked.GetValueOrDefault()
                    && !Option90Days.IsChecked.GetValueOrDefault());
        }

        private void ContentDialog_PrimaryButtonClick(
            ContentDialog sender,
            ContentDialogButtonClickEventArgs args)
        {
            if (Option24Hours.IsChecked == true)
            {
                SelectedDuration = GroupEphemeralDuration.Hours24;
            }
            else if (Option7Days.IsChecked == true)
            {
                SelectedDuration = GroupEphemeralDuration.Days7;
            }
            else if (Option90Days.IsChecked == true)
            {
                SelectedDuration = GroupEphemeralDuration.Days90;
            }
            else
            {
                SelectedDuration = GroupEphemeralDuration.Off;
            }
        }
    }
}
