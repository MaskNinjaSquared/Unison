using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Dialogs
{
    /// <summary>
    /// Status privacy radios. The two list options close the dialog with a pick request
    /// (every click, even on the option already checked) so the composer can open its picker.
    /// </summary>
    public sealed partial class StatusPrivacyDialog : ContentDialog
    {
        private StatusComposerViewModel _composer;

        public StatusPrivacyDialog()
        {
            InitializeComponent();
        }

        /// <summary>Set when a list option was clicked; null means Confirm / Cancel decided.</summary>
        public StatusPrivacyDialogChoice? PickRequest { get; private set; }

        public void Bind(StatusComposerViewModel composer)
        {
            _composer = composer;
            DataContext = composer;
            PickRequest = null;
            OptionFor(composer?.PendingAudienceMode ?? StatusAudienceMode.Contacts).IsChecked = true;
        }

        private void ContactsOption_Checked(object sender, RoutedEventArgs e)
        {
            if (_composer != null)
            {
                _composer.PendingAudienceMode = StatusAudienceMode.Contacts;
            }
        }

        private void ExceptOption_Click(object sender, RoutedEventArgs e)
        {
            RequestPick(StatusPrivacyDialogChoice.PickExcluded);
        }

        private void OnlyOption_Click(object sender, RoutedEventArgs e)
        {
            RequestPick(StatusPrivacyDialogChoice.PickOnlyShareWith);
        }

        private void RequestPick(StatusPrivacyDialogChoice choice)
        {
            PickRequest = choice;
            Hide();
        }

        private RadioButton OptionFor(StatusAudienceMode mode)
        {
            if (mode == StatusAudienceMode.ContactsExcept)
            {
                return ExceptOption;
            }

            if (mode == StatusAudienceMode.OnlyShareWith)
            {
                return OnlyOption;
            }

            return ContactsOption;
        }
    }
}
