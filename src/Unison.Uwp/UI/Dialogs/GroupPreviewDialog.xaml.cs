using System;
using Unison.Core.ViewModels;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Dialogs
{
    public sealed partial class GroupPreviewDialog : ContentDialog
    {
        public GroupPreviewDialog()
        {
            InitializeComponent();
        }

        public GroupPreviewDialogViewModel ViewModel =>
            DataContext as GroupPreviewDialogViewModel;

        private async void ContentDialog_PrimaryButtonClick(
            ContentDialog sender,
            ContentDialogButtonClickEventArgs args)
        {
            GroupPreviewDialogViewModel vm = ViewModel;
            if (vm == null)
            {
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            try
            {
                bool joined = await vm.ExecutePrimaryAsync();
                if (!joined)
                {
                    args.Cancel = true;
                }
            }
            catch (Exception)
            {
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
