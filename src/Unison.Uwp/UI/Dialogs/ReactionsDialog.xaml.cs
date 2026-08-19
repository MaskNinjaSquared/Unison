using Unison.Core.ViewModels;
using Unison.Uwp.Helpers;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Dialogs
{
    /// <summary>
    /// Read-only reactions viewer: tally chips over the list of who reacted.
    /// Bound to a <see cref="MessageReactionsViewModel"/> already loaded by DialogService.
    /// </summary>
    public sealed partial class ReactionsDialog : ContentDialog
    {
        public ReactionsDialog()
        {
            this.InitializeComponent();
            CloseButtonText = LocalizedStrings.Get("Common_Close", "Close");
        }

        public void Bind(MessageReactionsViewModel viewModel)
        {
            DataContext = viewModel;
            if (viewModel == null)
            {
                return;
            }

            Title = viewModel.Title;
            ChipsList.ItemsSource = viewModel.Chips;
            AuthorsList.ItemsSource = viewModel.Authors;
        }
    }
}
