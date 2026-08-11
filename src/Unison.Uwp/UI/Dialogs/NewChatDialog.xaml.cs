using System;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Unison.Uwp.Helpers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Dialogs
{
    /// <summary>
    /// ContentDialog for starting a chat by phone number.
    /// Bound to <see cref="NewChatViewModel"/> supplied by DialogService.
    /// </summary>
    public sealed partial class NewChatDialog : ContentDialog
    {
        private NewChatViewModel ViewModel => DataContext as NewChatViewModel;

        public string ResolvedJid { get; private set; }

        public NewChatDialog()
        {
            this.InitializeComponent();
        }

        private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                if (ViewModel == null && App.Services != null)
                {
                    DataContext = App.Services.GetRequiredService<NewChatViewModel>();
                }

                if (ViewModel == null)
                {
                    args.Cancel = true;
                    return;
                }

                ViewModel.PhoneNumber = PhoneNumberBox.Text;
                await ViewModel.SearchContactAsync();

                if (!string.IsNullOrEmpty(ViewModel.ResolvedJid))
                {
                    ResolvedJid = ViewModel.ResolvedJid;
                }
                else
                {
                    ErrorText.Text = ViewModel.ErrorMessage ?? LocalizedStrings.Get("NewChat_NotFound");
                    ErrorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
            }
            catch (Exception ex)
            {
                ErrorText.Text = LocalizedStrings.Format("NewChat_Error", ex.Message);
                ErrorText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
