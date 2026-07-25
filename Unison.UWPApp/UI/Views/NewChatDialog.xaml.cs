using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Unison.UWPApp.Services;

namespace Unison.UWPApp.UI.Views
{
    public sealed partial class NewChatDialog : ContentDialog
    {
        public string ResolvedJid { get; private set; }

        public NewChatDialog()
        {
            this.InitializeComponent();
        }

        private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            string phone = PhoneNumberBox.Text;

            if (string.IsNullOrWhiteSpace(phone))
            {
                ErrorText.Text = "Please enter a phone number.";
                ErrorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                deferral.Complete();
                return;
            }

            try
            {
                ErrorText.Text = "Searching...";
                ErrorText.Visibility = Visibility.Visible;
                
                var jid = await WhatsAppService.Instance.SearchContactAsync(phone);
                if (jid != null)
                {
                    ResolvedJid = jid;
                    // Success, dialog will close
                }
                else
                {
                    ErrorText.Text = "Could not find a WhatsApp account for this number.";
                    ErrorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
            }
            catch (Exception ex)
            {
                ErrorText.Text = $"Error: {ex.Message}";
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
