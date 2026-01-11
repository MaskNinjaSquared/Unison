using System;
using Unison.UWPApp.Client;
using Unison.UWPApp.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.UWPApp.UI.Views
{
    public sealed partial class DebugView : UserControl
    {
        public event EventHandler BackRequested;

        public DebugView()
        {
            this.InitializeComponent();
            SessionLoggingToggle.IsOn = SessionLogger.Instance.Enabled;
            SessionLogText.Text = SessionLogger.Instance.GetLogText();
            SessionLogger.Instance.OnLogUpdated += Instance_OnLogUpdated;
        }

        private void Instance_OnLogUpdated(object sender, string e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                SessionLogText.Text += e + "\n";
                // Auto scroll to bottom
                SessionLogScroller.ChangeView(null, SessionLogScroller.ScrollableHeight, null);
            });
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        private void SessionLoggingToggle_Toggled(object sender, RoutedEventArgs e)
        {
            SessionLogger.Instance.Enabled = SessionLoggingToggle.IsOn;
        }

        private async void SaveSessionLogButton_Click(object sender, RoutedEventArgs e)
        {
            await SessionLogger.Instance.SaveToFileAsync();
        }

        private void ClearSessionLogButton_Click(object sender, RoutedEventArgs e)
        {
            SessionLogger.Instance.Clear();
            SessionLogText.Text = "";
        }

        private void TestDHButton_Click(object sender, RoutedEventArgs e)
        {
            // Placeholder for original TestDH logic if needed
        }

        private async void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Wipe Session?",
                Content = "This will delete all local authentication data and close the app. You will need to re-pair.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel"
            };

            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await WhatsAppService.Instance.ClearSessionAsync();
                    Application.Current.Exit();
                }
            }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070057) || ex.Message.Contains("single ContentDialog"))
            {
                // Another dialog is already open - ignore this request
                System.Diagnostics.Debug.WriteLine("[DebugView] Cannot show session wipe dialog - another dialog is open.");
            }
        }
    }
}
