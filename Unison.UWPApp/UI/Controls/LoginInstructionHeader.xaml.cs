using System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml;
using Unison.UWPApp.Services;

namespace Unison.UWPApp.UI.Controls
{
    public sealed partial class LoginInstructionHeader : UserControl
    {
        public event EventHandler BackRequested;
        private int _tapCount = 0;

        public LoginInstructionHeader()
        {
            this.InitializeComponent();
        }

        private async void Logo_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _tapCount++;
            if (_tapCount >= 5)
            {
                _tapCount = 0;
                System.Diagnostics.Debug.WriteLine("[LoginHeader] Hidden reset triggered via logo taps");
                
                // Trigger full session wipe
                await WhatsAppService.Instance.ClearSessionAsync();
                
                var dialog = new ContentDialog
                {
                    Title = "Developer Reset",
                    Content = "Session and local data have been wiped. The app will now close for a clean restart.",
                    CloseButtonText = "OK"
                };
                await dialog.ShowAsync();
                Application.Current.Exit();
            }
        }
    }
}
