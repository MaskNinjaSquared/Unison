using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.ViewModels;
using Unison.Uwp.Helpers;
using Unison.Uwp.UI.Dialogs;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// WinRT ContentDialog adapter. Methods that need form state receive the
    /// target ViewModel (Imgur: ShowCustomApiKeyDialog(SettingsViewModel)).
    /// </summary>
    public class DialogService : IDialogService
    {
        public async Task<bool> ShowConfirmAsync(
            string title,
            string content,
            string primaryButtonText,
            string closeButtonText)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = content,
                    PrimaryButtonText = primaryButtonText,
                    CloseButtonText = closeButtonText
                };

                var result = await dialog.ShowAsync();
                return result == ContentDialogResult.Primary;
            }
            catch (System.Runtime.InteropServices.COMException ex)
                when (ex.Message.Contains("single ContentDialog") ||
                      ex.HResult == unchecked((int)0x80070057))
            {
                System.Diagnostics.Debug.WriteLine("[DialogService] Another dialog is already open.");
                return false;
            }
        }

        public async Task ShowMessageAsync(string title, string content, string closeButtonText)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = content,
                    CloseButtonText = closeButtonText
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DialogService] ShowMessageAsync: " + ex.Message);
            }
        }

        public async Task<string> ShowInputAsync(
            string title,
            string prompt,
            string placeholder,
            string primaryButtonText,
            string closeButtonText)
        {
            try
            {
                var input = new TextBox
                {
                    PlaceholderText = placeholder ?? string.Empty,
                    InputScope = new InputScope
                    {
                        Names = { new InputScopeName(InputScopeNameValue.TelephoneNumber) }
                    }
                };

                var panel = new StackPanel();
                if (!string.IsNullOrEmpty(prompt))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = prompt,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 8)
                    });
                }
                panel.Children.Add(input);

                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = panel,
                    PrimaryButtonText = primaryButtonText,
                    CloseButtonText = closeButtonText
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                {
                    return null;
                }

                return input.Text;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DialogService] ShowInputAsync: " + ex.Message);
                return null;
            }
        }

        public async Task ShowPairingCodeAsync(LoginViewModel loginVm, string code)
        {
            // loginVm kept for Imgur-style targeting / future expansion of the dialog.
            string shown = string.IsNullOrEmpty(code) ? "—" : code;
            await ShowMessageAsync(
                LocalizedStrings.Get("Login_PairPhoneCodeTitle"),
                LocalizedStrings.Format("Login_PairPhoneCodeBody", shown),
                LocalizedStrings.Get("Common_OK"));
        }

        public async Task<string> ShowNewChatDialogAsync(NewChatDialogViewModel newChatVm)
        {
            if (newChatVm == null)
            {
                throw new ArgumentNullException(nameof(newChatVm));
            }

            var dialog = new NewChatDialog
            {
                DataContext = newChatVm
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !string.IsNullOrEmpty(dialog.ResolvedJid))
            {
                return dialog.ResolvedJid;
            }

            return null;
        }

        public async Task<bool> ShowImageSendPreviewAsync(byte[] imageBytes, string infoText)
        {
            try
            {
                var dialog = new ImageSendPreviewDialog();
                await dialog.SetPreviewAsync(imageBytes, infoText);
                var result = await dialog.ShowAsync();
                return result == ContentDialogResult.Primary;
            }
            catch (System.Runtime.InteropServices.COMException ex)
                when (ex.Message.Contains("single ContentDialog") ||
                      ex.HResult == unchecked((int)0x80070057))
            {
                System.Diagnostics.Debug.WriteLine("[DialogService] Another dialog is already open (image preview).");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DialogService] ShowImageSendPreviewAsync: " + ex.Message);
                return false;
            }
        }

        public async Task ShowQrFullscreenAsync(string qrData)
        {
            if (string.IsNullOrEmpty(qrData))
            {
                return;
            }

            try
            {
                var dialog = new QrCodeFullscreenDialog();
                dialog.SetQrPayload(qrData);
                await dialog.ShowAsync();
            }
            catch (System.Runtime.InteropServices.COMException ex)
                when (ex.Message.Contains("single ContentDialog") ||
                      ex.HResult == unchecked((int)0x80070057))
            {
                System.Diagnostics.Debug.WriteLine("[DialogService] Another dialog is already open (QR fullscreen).");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DialogService] ShowQrFullscreenAsync: " + ex.Message);
            }
        }
    }
}
